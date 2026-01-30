using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Server;
using StrideShaderLanguageServer.Handlers;
using StrideShaderLanguageServer.Services;

namespace StrideShaderLanguageServer;

class Program
{
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
                .WithHandler<SignatureHelpHandler>()
                .WithHandler<CodeActionHandler>()
                .OnInitialize((server, request, token) =>
                {
                    var workspace = server.Services.GetRequiredService<ShaderWorkspace>();

                    // Initialize workspace with paths from client
                    if (request.WorkspaceFolders is { } folders)
                    {
                        foreach (var folder in folders)
                        {
                            workspace.AddWorkspaceFolder(folder.Uri.GetFileSystemPath());
                        }
                    }

                    // Auto-discover shader paths
                    workspace.DiscoverShaderPaths();

                    return Task.CompletedTask;
                })
                .OnInitialized((server, request, response, token) =>
                {
                    var logger = server.Services.GetRequiredService<ILogger<Program>>();
                    logger.LogInformation("Stride Shader Language Server initialized");

                    // Start indexing shaders in background
                    var workspace = server.Services.GetRequiredService<ShaderWorkspace>();
                    _ = Task.Run(() => workspace.IndexAllShaders());

                    return Task.CompletedTask;
                })
        ).ConfigureAwait(false);

        await server.WaitForExit.ConfigureAwait(false);
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
