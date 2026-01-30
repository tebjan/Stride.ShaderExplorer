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
                    SourceShader: definedIn,
                    IsStage: variable.IsStage,
                    IsEntryPoint: false  // Variables are never entry points
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

                // Check if this is a shader stage entry point
                var isEntryPoint = IsShaderStageEntryPoint(method.Name);

                var memberInfo = new MemberInfo(
                    Name: method.Name,
                    Type: method.ReturnType,
                    Signature: signature,
                    Comment: null,
                    Line: method.Location.Location.Line,
                    FilePath: filePath,
                    IsLocal: isLocal,
                    SourceShader: definedIn,
                    IsStage: method.IsStage,
                    IsEntryPoint: isEntryPoint
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
