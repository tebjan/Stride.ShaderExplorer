using MediatR;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol.General;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace StrideShaderLanguageServer.Handlers;

/// <summary>
/// Handles shutdown and exit requests from the client.
/// Ensures proper cleanup of background tasks and resources.
/// </summary>
public class ShutdownHandler : IShutdownHandler, IExitHandler
{
    private readonly ILogger<ShutdownHandler> _logger;
    private readonly TextDocumentSyncHandler _textDocumentSyncHandler;
    private readonly CancellationTokenSource _shutdownCts;

    public ShutdownHandler(
        ILogger<ShutdownHandler> logger,
        TextDocumentSyncHandler textDocumentSyncHandler,
        CancellationTokenSource shutdownCts)
    {
        _logger = logger;
        _textDocumentSyncHandler = textDocumentSyncHandler;
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
        _logger.LogInformation("Exit requested");

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

            if (_textDocumentSyncHandler is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        return Unit.Value;
    }
}
