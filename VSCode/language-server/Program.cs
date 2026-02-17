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
    private static TextDocumentSyncHandler? _textDocumentSyncHandler;
    private static ILogger<Program>? _logger;

    // Cancellation token for background tasks
    private static readonly CancellationTokenSource _shutdownCts = new();

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
                .WithHandler<DidChangeWatchedFilesHandler>()
                .WithHandler<ShutdownHandler>()
                .OnInitialize((server, request, token) =>
                {
                    // Store service references for custom handlers
                    _workspace = server.Services.GetRequiredService<ShaderWorkspace>();
                    _inheritanceResolver = server.Services.GetRequiredService<InheritanceResolver>();
                    _textDocumentSyncHandler = server.Services.GetRequiredService<TextDocumentSyncHandler>();
                    _logger = server.Services.GetRequiredService<ILogger<Program>>();

                    // Initialize workspace with paths from client
                    if (request.WorkspaceFolders is { } folders)
                    {
                        foreach (var folder in folders)
                        {
                            _workspace.AddWorkspaceFolder(folder.Uri.GetFileSystemPath());
                        }
                    }

                    // Read initialization options from client
                    if (request.InitializationOptions is Newtonsoft.Json.Linq.JObject initOptions)
                    {
                        // Configure diagnostics delay
                        if (initOptions.TryGetValue("diagnosticsDelayMs", out var delayToken))
                        {
                            var delayMs = delayToken.Value<int>();
                            _textDocumentSyncHandler.SetDiagnosticsDelay(delayMs);
                            _logger?.LogInformation("Diagnostics delay set to {DelayMs}ms", delayMs);
                        }

                        // Add user-configured additional shader paths
                        if (initOptions.TryGetValue("additionalShaderPaths", out var pathsToken))
                        {
                            var additionalPaths = pathsToken.ToObject<List<string>>() ?? [];
                            foreach (var path in additionalPaths)
                            {
                                if (Directory.Exists(path))
                                {
                                    _workspace.AddShaderSearchPath(path, Services.ShaderSource.Workspace);
                                    _logger?.LogInformation("Added additional shader path from settings: {Path}", path);
                                }
                                else
                                {
                                    _logger?.LogWarning("Additional shader path does not exist: {Path}", path);
                                }
                            }
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

                    // Start indexing shaders in background (with cancellation support)
                    _ = Task.Run(() =>
                    {
                        if (!_shutdownCts.Token.IsCancellationRequested)
                        {
                            _workspace?.IndexAllShaders();
                        }
                    }, _shutdownCts.Token);

                    return Task.CompletedTask;
                })
        ).ConfigureAwait(false);

        await server.WaitForExit.ConfigureAwait(false);

        // Final cleanup (in case OnShutdown wasn't called)
        _logger?.LogInformation("Server exiting, final cleanup...");

        // Signal all background tasks to stop
        if (!_shutdownCts.IsCancellationRequested)
        {
            await _shutdownCts.CancelAsync();
        }
        _shutdownCts.Dispose();

        // Dispose TextDocumentSyncHandler if not already disposed
        if (_textDocumentSyncHandler is IDisposable disposable)
        {
            disposable.Dispose();
        }

        _logger?.LogInformation("Server exit complete");

        // Explicitly exit the process to ensure termination
        // This is needed because on Windows with dotnet run, the process
        // may not terminate cleanly when VS Code closes the connection.
        // See: https://github.com/microsoft/vscode-languageserver-node/issues/850
        Environment.Exit(0);
    }

    private static InheritanceTreeResponse HandleInheritanceTreeRequest(InheritanceTreeParams request)
    {
        _logger?.LogDebug("Getting inheritance tree for {Uri}", request.Uri);

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

            // Build hierarchical tree of direct base shaders
            var visited = new HashSet<string>();
            var directBases = BuildHierarchicalInheritance(currentParsed.BaseShaderNames, visited);

            var currentNode = new ShaderNode(
                Name: currentParsed.Name,
                FilePath: currentShaderInfo.FilePath,
                Source: currentShaderInfo.DisplayPath,
                Line: 1,
                IsLocal: true,
                Children: directBases
            );

            // Also build flat list for backwards compatibility
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

            _logger?.LogDebug("Found {Count} base shaders for {ShaderName}", baseShaders.Count, shaderName);

            return new InheritanceTreeResponse(currentNode, baseShaders);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error getting inheritance tree");
            return new InheritanceTreeResponse(null, new List<ShaderNode>());
        }
    }

    /// <summary>
    /// Build a hierarchical tree of inheritance, where each shader node contains its direct bases as children.
    /// </summary>
    private static List<ShaderNode> BuildHierarchicalInheritance(IReadOnlyList<string> baseNames, HashSet<string> visited)
    {
        var result = new List<ShaderNode>();

        foreach (var baseName in baseNames)
        {
            if (visited.Contains(baseName))
                continue;
            visited.Add(baseName);

            var baseInfo = _workspace?.GetShaderByName(baseName);
            var baseParsed = _workspace?.GetParsedShader(baseName);

            if (baseInfo == null)
                continue;

            // Recursively get this shader's direct bases
            List<ShaderNode>? children = null;
            if (baseParsed != null && baseParsed.BaseShaderNames.Count > 0)
            {
                children = BuildHierarchicalInheritance(baseParsed.BaseShaderNames, visited);
            }

            result.Add(new ShaderNode(
                Name: baseName,
                FilePath: baseInfo.FilePath,
                Source: baseInfo.DisplayPath,
                Line: 1,
                IsLocal: false,
                Children: children?.Count > 0 ? children : null
            ));
        }

        return result;
    }

    private static ShaderMembersResponse HandleShaderMembersRequest(ShaderMembersParams request)
    {
        _logger?.LogDebug("Getting shader members for {Uri}", request.Uri);

        try
        {
            var uri = DocumentUri.From(request.Uri);
            var path = uri.GetFileSystemPath();
            var shaderName = Path.GetFileNameWithoutExtension(path);

            var currentParsed = _workspace?.GetParsedShader(shaderName) ?? _workspace?.GetParsedShader(path);

            if (currentParsed == null)
            {
                _logger?.LogWarning("Shader not found: {ShaderName} (path: {Path})", shaderName, path);
                return new ShaderMembersResponse([], [], [], []);
            }

            var streams = new List<MemberInfo>();
            var variableGroups = new Dictionary<string, List<MemberInfo>>();
            var methodGroups = new Dictionary<string, List<MemberInfo>>();
            var compositions = new List<CompositionInfo>();

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
                    Comment: variable.Documentation,
                    Line: variable.Location.Location.Line,
                    FilePath: filePath,
                    IsLocal: isLocal,
                    SourceShader: definedIn,
                    IsStage: variable.IsStage,
                    IsEntryPoint: false,  // Variables are never entry points
                    Semantic: variable.SemanticBinding  // e.g., "SV_Position", "TEXCOORD0"
                );

                if (variable.IsStream)
                {
                    streams.Add(memberInfo);
                }
                else if (!variable.IsCompose)  // Compositions shown separately
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

                // Include semantic bindings in parameter display
                var parameters = string.Join(", ", method.Parameters.Select(p =>
                {
                    var paramStr = $"{p.TypeName} {p.Name}";
                    if (!string.IsNullOrEmpty(p.SemanticBinding))
                        paramStr += $" : {p.SemanticBinding}";
                    return paramStr;
                }));
                var signature = $"({parameters})";

                // Check if this is a shader stage entry point
                var isEntryPoint = IsShaderStageEntryPoint(method.Name);

                // Collect parameter semantics for display (e.g., "POSITION, SV_Target")
                var paramSemantics = method.Parameters
                    .Where(p => !string.IsNullOrEmpty(p.SemanticBinding))
                    .Select(p => p.SemanticBinding)
                    .ToList();
                var semanticSummary = paramSemantics.Count > 0 ? string.Join(", ", paramSemantics) : null;

                var memberInfo = new MemberInfo(
                    Name: method.Name,
                    Type: method.ReturnType,
                    Signature: signature,
                    Comment: method.Documentation,
                    Line: method.Location.Location.Line,
                    FilePath: filePath,
                    IsLocal: isLocal,
                    SourceShader: definedIn,
                    IsStage: method.IsStage,
                    IsEntryPoint: isEntryPoint,
                    Semantic: semanticSummary
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

            // Process compositions
            foreach (var (composition, definedIn) in _inheritanceResolver?.GetAllCompositions(currentParsed) ?? [])
            {
                var shaderInfo = _workspace?.GetShaderByName(definedIn);
                var filePath = shaderInfo?.FilePath ?? "";
                var isLocal = definedIn == shaderName;

                compositions.Add(new CompositionInfo(
                    Name: composition.Name,
                    Type: composition.TypeName,
                    Line: composition.Location.Location.Line,
                    FilePath: filePath,
                    IsLocal: isLocal,
                    SourceShader: definedIn
                ));
            }

            _logger?.LogDebug("Found {StreamCount} streams, {VarGroups} variable groups, {MethodGroups} method groups, {CompCount} compositions",
                streams.Count, variables.Count, methods.Count, compositions.Count);

            return new ShaderMembersResponse(streams, variables, methods, compositions);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error getting shader members");
            return new ShaderMembersResponse([], [], [], []);
        }
    }

    /// <summary>
    /// Known shader stage entry point method names.
    /// These are the standard entry points for different shader stages.
    /// </summary>
    private static readonly HashSet<string> ShaderStageEntryPoints = new(StringComparer.OrdinalIgnoreCase)
    {
        "VSMain",      // Vertex Shader
        "HSMain",      // Hull Shader (Tessellation Control)
        "HSConstantMain", // Hull Shader constant function
        "DSMain",      // Domain Shader (Tessellation Evaluation)
        "GSMain",      // Geometry Shader
        "PSMain",      // Pixel/Fragment Shader
        "CSMain",      // Compute Shader
        "ShadeVertex", // Alternative vertex shader entry
        "ShadePixel",  // Alternative pixel shader entry
    };

    /// <summary>
    /// Check if a method name is a shader stage entry point.
    /// </summary>
    private static bool IsShaderStageEntryPoint(string methodName)
    {
        return ShaderStageEntryPoints.Contains(methodName);
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

        // Provide the shutdown CancellationTokenSource for cleanup
        services.AddSingleton(_shutdownCts);
    }
}
