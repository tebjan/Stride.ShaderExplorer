using MediatR;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.JsonRpc;
using OmniSharp.Extensions.LanguageServer.Protocol;
using StrideShaderLanguageServer.Services;

namespace StrideShaderLanguageServer.Handlers;

#region Request/Response Types

// Inheritance Tree Request
public record InheritanceTreeParams(string Uri) : IRequest<InheritanceTreeResponse>;

public record ShaderNode(
    string Name,
    string FilePath,
    string Source,
    int Line,
    bool IsLocal
);

public record InheritanceTreeResponse(
    ShaderNode? CurrentShader,
    List<ShaderNode> BaseShaders
);

// Shader Members Request
public record ShaderMembersParams(string Uri) : IRequest<ShaderMembersResponse>;

public record MemberInfo(
    string Name,
    string Type,
    string Signature,
    string? Comment,
    int Line,
    string FilePath,
    bool IsLocal
);

public record MemberGroup(
    string SourceShader,
    string FilePath,
    List<MemberInfo> Members,
    bool IsLocal
);

public record ShaderMembersResponse(
    List<MemberInfo> Streams,
    List<MemberGroup> Variables,
    List<MemberGroup> Methods
);

#endregion

/// <summary>
/// Handler for stride/getInheritanceTree custom LSP request.
/// Returns the inheritance chain for the shader at the given URI.
/// </summary>
[Method("stride/getInheritanceTree", Direction.ClientToServer)]
public class InheritanceTreeHandler : IJsonRpcRequestHandler<InheritanceTreeParams, InheritanceTreeResponse>
{
    private readonly ILogger<InheritanceTreeHandler> _logger;
    private readonly ShaderWorkspace _workspace;
    private readonly InheritanceResolver _inheritanceResolver;

    public InheritanceTreeHandler(
        ILogger<InheritanceTreeHandler> logger,
        ShaderWorkspace workspace,
        InheritanceResolver inheritanceResolver)
    {
        _logger = logger;
        _workspace = workspace;
        _inheritanceResolver = inheritanceResolver;
    }

    public Task<InheritanceTreeResponse> Handle(InheritanceTreeParams request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Getting inheritance tree for {Uri}", request.Uri);

        try
        {
            var uri = DocumentUri.From(request.Uri);
            var path = uri.GetFileSystemPath();
            var shaderName = Path.GetFileNameWithoutExtension(path);

            // Try by name first, then by path
            var currentShaderInfo = _workspace.GetShaderByName(shaderName) ?? _workspace.GetShaderByPath(path);
            var currentParsed = _workspace.GetParsedShader(shaderName) ?? _workspace.GetParsedShader(path);

            if (currentParsed == null || currentShaderInfo == null)
            {
                _logger.LogWarning("Shader not found: {ShaderName} (path: {Path})", shaderName, path);
                return Task.FromResult(new InheritanceTreeResponse(null, new List<ShaderNode>()));
            }

            // Current shader node - use DisplayPath for UI, but keep FilePath for navigation
            var currentNode = new ShaderNode(
                Name: currentParsed.Name,
                FilePath: currentShaderInfo.FilePath,
                Source: currentShaderInfo.DisplayPath, // Use DisplayPath for cleaner UI
                Line: 1,
                IsLocal: true
            );

            // Get base shaders
            var baseShaders = new List<ShaderNode>();
            var inheritanceChain = _inheritanceResolver.ResolveInheritanceChain(shaderName);

            foreach (var baseShader in inheritanceChain)
            {
                var baseInfo = _workspace.GetShaderByName(baseShader.Name);
                if (baseInfo != null)
                {
                    baseShaders.Add(new ShaderNode(
                        Name: baseShader.Name,
                        FilePath: baseInfo.FilePath,
                        Source: baseInfo.DisplayPath, // Use DisplayPath for cleaner UI
                        Line: 1,
                        IsLocal: false
                    ));
                }
            }

            _logger.LogInformation("Found {Count} base shaders for {ShaderName}", baseShaders.Count, shaderName);

            return Task.FromResult(new InheritanceTreeResponse(currentNode, baseShaders));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting inheritance tree");
            return Task.FromResult(new InheritanceTreeResponse(null, new List<ShaderNode>()));
        }
    }
}

/// <summary>
/// Handler for stride/getShaderMembers custom LSP request.
/// Returns streams, variables, and methods for the shader at the given URI.
/// </summary>
[Method("stride/getShaderMembers", Direction.ClientToServer)]
public class ShaderMembersHandler : IJsonRpcRequestHandler<ShaderMembersParams, ShaderMembersResponse>
{
    private readonly ILogger<ShaderMembersHandler> _logger;
    private readonly ShaderWorkspace _workspace;
    private readonly InheritanceResolver _inheritanceResolver;

    public ShaderMembersHandler(
        ILogger<ShaderMembersHandler> logger,
        ShaderWorkspace workspace,
        InheritanceResolver inheritanceResolver)
    {
        _logger = logger;
        _workspace = workspace;
        _inheritanceResolver = inheritanceResolver;
    }

    public Task<ShaderMembersResponse> Handle(ShaderMembersParams request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Getting shader members for {Uri}", request.Uri);

        try
        {
            var uri = DocumentUri.From(request.Uri);
            var path = uri.GetFileSystemPath();
            var shaderName = Path.GetFileNameWithoutExtension(path);

            // Try by name first, then by path
            var currentParsed = _workspace.GetParsedShader(shaderName) ?? _workspace.GetParsedShader(path);

            if (currentParsed == null)
            {
                _logger.LogWarning("Shader not found: {ShaderName} (path: {Path})", shaderName, path);
                return Task.FromResult(new ShaderMembersResponse(
                    new List<MemberInfo>(),
                    new List<MemberGroup>(),
                    new List<MemberGroup>()
                ));
            }

            // Collect streams
            var streams = new List<MemberInfo>();

            // Collect variables grouped by source shader
            var variableGroups = new Dictionary<string, List<MemberInfo>>();

            // Collect methods grouped by source shader
            var methodGroups = new Dictionary<string, List<MemberInfo>>();

            // Process all variables (local + inherited)
            foreach (var (variable, definedIn) in _inheritanceResolver.GetAllVariables(currentParsed))
            {
                var shaderInfo = _workspace.GetShaderByName(definedIn);
                var filePath = shaderInfo?.FilePath ?? "";
                var isLocal = definedIn == shaderName;

                var memberInfo = new MemberInfo(
                    Name: variable.Name,
                    Type: variable.TypeName,
                    Signature: "",
                    Comment: null, // TODO: Extract comments from parsed shader
                    Line: variable.Location.Location.Line,
                    FilePath: filePath,
                    IsLocal: isLocal
                );

                // Streams are collected separately
                if (variable.IsStream)
                {
                    streams.Add(memberInfo);
                }
                else
                {
                    if (!variableGroups.ContainsKey(definedIn))
                        variableGroups[definedIn] = new List<MemberInfo>();
                    variableGroups[definedIn].Add(memberInfo);
                }
            }

            // Process all methods (local + inherited)
            foreach (var (method, definedIn) in _inheritanceResolver.GetAllMethods(currentParsed))
            {
                var shaderInfo = _workspace.GetShaderByName(definedIn);
                var filePath = shaderInfo?.FilePath ?? "";
                var isLocal = definedIn == shaderName;

                var parameters = string.Join(", ", method.Parameters.Select(p => $"{p.TypeName} {p.Name}"));
                var signature = $"({parameters})";

                var memberInfo = new MemberInfo(
                    Name: method.Name,
                    Type: method.ReturnType,
                    Signature: signature,
                    Comment: null,
                    Line: method.Location.Location.Line,
                    FilePath: filePath,
                    IsLocal: isLocal
                );

                if (!methodGroups.ContainsKey(definedIn))
                    methodGroups[definedIn] = new List<MemberInfo>();
                methodGroups[definedIn].Add(memberInfo);
            }

            // Convert to MemberGroup lists, sorted: local first, then inherited
            var variables = variableGroups
                .OrderByDescending(g => g.Key == shaderName) // Local first
                .ThenBy(g => g.Key)
                .Select(g =>
                {
                    var info = _workspace.GetShaderByName(g.Key);
                    return new MemberGroup(
                        SourceShader: g.Key,
                        FilePath: info?.FilePath ?? "",
                        Members: g.Value,
                        IsLocal: g.Key == shaderName
                    );
                })
                .ToList();

            var methods = methodGroups
                .OrderByDescending(g => g.Key == shaderName) // Local first
                .ThenBy(g => g.Key)
                .Select(g =>
                {
                    var info = _workspace.GetShaderByName(g.Key);
                    return new MemberGroup(
                        SourceShader: g.Key,
                        FilePath: info?.FilePath ?? "",
                        Members: g.Value,
                        IsLocal: g.Key == shaderName
                    );
                })
                .ToList();

            _logger.LogInformation("Found {StreamCount} streams, {VarGroups} variable groups, {MethodGroups} method groups for {ShaderName}",
                streams.Count, variables.Count, methods.Count, shaderName);

            return Task.FromResult(new ShaderMembersResponse(streams, variables, methods));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting shader members");
            return Task.FromResult(new ShaderMembersResponse(
                new List<MemberInfo>(),
                new List<MemberGroup>(),
                new List<MemberGroup>()
            ));
        }
    }
}

