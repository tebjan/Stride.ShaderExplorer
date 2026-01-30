using MediatR;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol.General;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using StrideShaderLanguageServer.Services;

namespace StrideShaderLanguageServer.Handlers;

/// <summary>
/// Handles shutdown and exit requests from the client.
/// Ensures proper cleanup of background tasks and resources.
/// </summary>
public class ShutdownHandler : IShutdownHandler, IExitHandler
{
    private readonly ILogger<ShutdownHandler> _logger;
    private readonly TextDocumentSyncHandler _textDocumentSyncHandler;
    private readonly ShaderWorkspace _workspace;
    private readonly CancellationTokenSource _shutdownCts;

    public ShutdownHandler(
        ILogger<ShutdownHandler> logger,
        TextDocumentSyncHandler textDocumentSyncHandler,
        ShaderWorkspace workspace,
        CancellationTokenSource shutdownCts)
    {
        _logger = logger;
        _textDocumentSyncHandler = textDocumentSyncHandler;
        _workspace = workspace;
        _shutdownCts = shutdownCts;
    }

    public async Task<Unit> Handle(ShutdownParams request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Shutdown requested, cleaning up...");

        // Signal all background tasks to stop
        try
        {
            await _shutdownCts.CancelAsync();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed, that's fine
        }

        // Stop background parsing
        _workspace.StopBackgroundParsing();

        // Dispose the TextDocumentSyncHandler to clean up pending diagnostic tasks
        if (_textDocumentSyncHandler is IDisposable disposable)
        {
            disposable.Dispose();
        }

        _logger.LogInformation("Cleanup complete");
        return Unit.Value;
    }

    public async Task<Unit> Handle(ExitParams request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Exit requested, terminating process...");

        // Make sure cleanup happened
        if (!_shutdownCts.IsCancellationRequested)
        {
            try
            {
                await _shutdownCts.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed
            }

            _workspace.StopBackgroundParsing();

            if (_textDocumentSyncHandler is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        // Explicitly exit the process - this is required because:
        // 1. VS Code's client.stop() doesn't always terminate the process on Windows
        // 2. When using dotnet run, the child process may not receive termination signals
        // See: https://github.com/microsoft/vscode-languageserver-node/issues/850
        _logger.LogInformation("Calling Environment.Exit(0)");

        // Schedule the exit on a background thread to allow the response to be sent first
        _ = Task.Run(async () =>
        {
            await Task.Delay(100); // Give time for the exit response to be sent
            Environment.Exit(0);
        });

        return Unit.Value;
    }
}
