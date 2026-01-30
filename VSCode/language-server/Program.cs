using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using OmniSharp.Extensions.JsonRpc;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Server;
using StrideShaderLanguageServer.Handlers;
using StrideShaderLanguageServer.Services;

namespace StrideShaderLanguageServer;

class Program
{
    // Static references to services for custom handlers
    private static ShaderWorkspace? _workspace;
    private static InheritanceResolver? _inheritanceResolver;
    private static ILogger<Program>? _logger;

    // JSON serializer with camelCase for TypeScript compatibility
    private static readonly JsonSerializer CamelCaseSerializer = JsonSerializer.Create(new JsonSerializerSettings
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver()
    });

    static async Task Main(string[] args)
    {
        var server = await LanguageServer.From(options =>
            options
                .WithInput(Console.OpenStandardInput())
                .WithOutput(Console.OpenStandardOutput())
                .ConfigureLogging(builder => builder
                    .AddLanguageProtocolLogging()
                    .SetMinimumLevel(args.Contains("--debug") ? LogLevel.Debug : LogLevel.Information))
                .WithServices(ConfigureServices)
                .WithHandler<TextDocumentSyncHandler>()
                .WithHandler<CompletionHandler>()
                .WithHandler<HoverHandler>()
                .WithHandler<DefinitionHandler>()
                .WithHandler<SignatureHelpHandler>()
                .WithHandler<CodeActionHandler>()
                .WithHandler<SemanticTokensHandler>()
                .OnInitialize((server, request, token) =>
                {
                    // Store service references for custom handlers
                    _workspace = server.Services.GetRequiredService<ShaderWorkspace>();
                    _inheritanceResolver = server.Services.GetRequiredService<InheritanceResolver>();
                    _logger = server.Services.GetRequiredService<ILogger<Program>>();

                    // Initialize workspace with paths from client
                    if (request.WorkspaceFolders is { } folders)
                    {
                        foreach (var folder in folders)
                        {
                            _workspace.AddWorkspaceFolder(folder.Uri.GetFileSystemPath());
                        }
                    }

                    // Auto-discover shader paths
                    _workspace.DiscoverShaderPaths();

                    return Task.CompletedTask;
                })
                .OnInitialized((server, request, response, token) =>
                {
                    _logger?.LogInformation("Stride Shader Language Server initialized");

                    // Register custom panel handlers dynamically after initialization
                    server.Register(registry =>
                    {
                        registry.OnJsonRequest(
                            "stride/getInheritanceTree",
                            (JToken token) =>
                            {
                                var request = token.ToObject<InheritanceTreeParams>()!;
                                var result = HandleInheritanceTreeRequest(request);
                                return Task.FromResult(JToken.FromObject(result, CamelCaseSerializer));
                            });

                        registry.OnJsonRequest(
                            "stride/getShaderMembers",
                            (JToken token) =>
                            {
                                var request = token.ToObject<ShaderMembersParams>()!;
                                var result = HandleShaderMembersRequest(request);
                                return Task.FromResult(JToken.FromObject(result, CamelCaseSerializer));
                            });
                    });

                    _logger?.LogInformation("Custom panel handlers registered: stride/getInheritanceTree, stride/getShaderMembers");

                    // Start indexing shaders in background
                    _ = Task.Run(() => _workspace?.IndexAllShaders());

                    return Task.CompletedTask;
                })
        ).ConfigureAwait(false);

        await server.WaitForExit.ConfigureAwait(false);
    }

    private static InheritanceTreeResponse HandleInheritanceTreeRequest(InheritanceTreeParams request)
    {
        _logger?.LogInformation("Getting inheritance tree for {Uri}", request.Uri);

        try
        {
            var uri = DocumentUri.From(request.Uri);
            var path = uri.GetFileSystemPath();
            var shaderName = Path.GetFileNameWithoutExtension(path);

            var currentShaderInfo = _workspace?.GetShaderByName(shaderName) ?? _workspace?.GetShaderByPath(path);
            var currentParsed = _workspace?.GetParsedShader(shaderName) ?? _workspace?.GetParsedShader(path);

            if (currentParsed == null || currentShaderInfo == null)
            {
                _logger?.LogWarning("Shader not found: {ShaderName} (path: {Path})", shaderName, path);
                return new InheritanceTreeResponse(null, new List<ShaderNode>());
            }

            var currentNode = new ShaderNode(
                Name: currentParsed.Name,
                FilePath: currentShaderInfo.FilePath,
                Source: currentShaderInfo.DisplayPath,
                Line: 1,
                IsLocal: true
            );

            var baseShaders = new List<ShaderNode>();
            var inheritanceChain = _inheritanceResolver?.ResolveInheritanceChain(shaderName) ?? [];

            foreach (var baseShader in inheritanceChain)
            {
                var baseInfo = _workspace?.GetShaderByName(baseShader.Name);
                if (baseInfo != null)
                {
                    baseShaders.Add(new ShaderNode(
                        Name: baseShader.Name,
                        FilePath: baseInfo.FilePath,
                        Source: baseInfo.DisplayPath,
                        Line: 1,
                        IsLocal: false
                    ));
                }
            }

            _logger?.LogInformation("Found {Count} base shaders for {ShaderName}", baseShaders.Count, shaderName);

            return new InheritanceTreeResponse(currentNode, baseShaders);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error getting inheritance tree");
            return new InheritanceTreeResponse(null, new List<ShaderNode>());
        }
    }

    private static ShaderMembersResponse HandleShaderMembersRequest(ShaderMembersParams request)
    {
        _logger?.LogInformation("Getting shader members for {Uri}", request.Uri);

        try
        {
            var uri = DocumentUri.From(request.Uri);
            var path = uri.GetFileSystemPath();
            var shaderName = Path.GetFileNameWithoutExtension(path);

            var currentParsed = _workspace?.GetParsedShader(shaderName) ?? _workspace?.GetParsedShader(path);

            if (currentParsed == null)
            {
                _logger?.LogWarning("Shader not found: {ShaderName} (path: {Path})", shaderName, path);
                return new ShaderMembersResponse([], [], []);
            }

            var streams = new List<MemberInfo>();
            var variableGroups = new Dictionary<string, List<MemberInfo>>();
            var methodGroups = new Dictionary<string, List<MemberInfo>>();

            // Process all variables
            foreach (var (variable, definedIn) in _inheritanceResolver?.GetAllVariables(currentParsed) ?? [])
            {
                var shaderInfo = _workspace?.GetShaderByName(definedIn);
                var filePath = shaderInfo?.FilePath ?? "";
                var isLocal = definedIn == shaderName;

                var memberInfo = new MemberInfo(
                    Name: variable.Name,
                    Type: variable.TypeName,
                    Signature: "",
                    Comment: null,
                    Line: variable.Location.Location.Line,
                    FilePath: filePath,
                    IsLocal: isLocal,
                    SourceShader: definedIn
                );

                if (variable.IsStream)
                {
                    streams.Add(memberInfo);
                }
                else
                {
                    if (!variableGroups.ContainsKey(definedIn))
                        variableGroups[definedIn] = [];
                    variableGroups[definedIn].Add(memberInfo);
                }
            }

            // Process all methods
            foreach (var (method, definedIn) in _inheritanceResolver?.GetAllMethods(currentParsed) ?? [])
            {
                var shaderInfo = _workspace?.GetShaderByName(definedIn);
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
                    IsLocal: isLocal,
                    SourceShader: definedIn
                );

                if (!methodGroups.ContainsKey(definedIn))
                    methodGroups[definedIn] = [];
                methodGroups[definedIn].Add(memberInfo);
            }

            var variables = variableGroups
                .OrderByDescending(g => g.Key == shaderName)
                .ThenBy(g => g.Key)
                .Select(g =>
                {
                    var info = _workspace?.GetShaderByName(g.Key);
                    return new MemberGroup(
                        SourceShader: g.Key,
                        FilePath: info?.FilePath ?? "",
                        Members: g.Value,
                        IsLocal: g.Key == shaderName
                    );
                })
                .ToList();

            var methods = methodGroups
                .OrderByDescending(g => g.Key == shaderName)
                .ThenBy(g => g.Key)
                .Select(g =>
                {
                    var info = _workspace?.GetShaderByName(g.Key);
                    return new MemberGroup(
                        SourceShader: g.Key,
                        FilePath: info?.FilePath ?? "",
                        Members: g.Value,
                        IsLocal: g.Key == shaderName
                    );
                })
                .ToList();

            _logger?.LogInformation("Found {StreamCount} streams, {VarGroups} variable groups, {MethodGroups} method groups",
                streams.Count, variables.Count, methods.Count);

            return new ShaderMembersResponse(streams, variables, methods);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error getting shader members");
            return new ShaderMembersResponse([], [], []);
        }
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // Core services
        services.AddSingleton<ShaderParser>();
        services.AddSingleton<ShaderWorkspace>();
        services.AddSingleton<InheritanceResolver>();
        services.AddSingleton<CompletionService>();
        services.AddSingleton<StrideInternalsAccessor>();
        services.AddSingleton<SemanticValidator>();

        // TextDocumentSyncHandler as singleton so other handlers can access document content
        services.AddSingleton<TextDocumentSyncHandler>();
    }
}
