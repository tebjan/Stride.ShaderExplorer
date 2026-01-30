using MediatR;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using OmniSharp.Extensions.LanguageServer.Protocol.Server.Capabilities;
using StrideShaderLanguageServer.Services;


namespace StrideShaderLanguageServer.Handlers;

public class TextDocumentSyncHandler : TextDocumentSyncHandlerBase, IDisposable
{
    private readonly ILogger<TextDocumentSyncHandler> _logger;
    private readonly ShaderWorkspace _workspace;
    private readonly ShaderParser _parser;
    private readonly SemanticValidator _semanticValidator;
    private readonly ILanguageServerFacade _server;
    private readonly Dictionary<DocumentUri, string> _documentContents = new();

    // Debouncing: delay diagnostics until user stops typing
    private readonly Dictionary<DocumentUri, CancellationTokenSource> _diagnosticDebounce = new();
    private int _diagnosticDelayMs = 3000; // Default: wait 3 seconds after last keystroke

    private bool _disposed;

    /// <summary>
    /// Set the diagnostics delay from client settings.
    /// </summary>
    public void SetDiagnosticsDelay(int delayMs)
    {
        _diagnosticDelayMs = Math.Clamp(delayMs, 500, 10000);
        _logger.LogInformation("Diagnostics delay configured to {DelayMs}ms", _diagnosticDelayMs);
    }

    public TextDocumentSyncHandler(
        ILogger<TextDocumentSyncHandler> logger,
        ShaderWorkspace workspace,
        ShaderParser parser,
        SemanticValidator semanticValidator,
        ILanguageServerFacade server)
    {
        _logger = logger;
        _workspace = workspace;
        _parser = parser;
        _semanticValidator = semanticValidator;
        _server = server;
    }

    public override TextDocumentAttributes GetTextDocumentAttributes(DocumentUri uri)
    {
        return new TextDocumentAttributes(uri, "sdsl");
    }

    public override Task<Unit> Handle(DidOpenTextDocumentParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri;
        var content = request.TextDocument.Text;

        _logger.LogDebug("Document opened: {Uri}", uri);
        _documentContents[uri] = content;

        // Update workspace and publish diagnostics immediately on open
        var path = uri.GetFileSystemPath();
        _workspace.UpdateDocument(path, content);
        PublishDiagnostics(uri, content);

        return Unit.Task;
    }

    public override async Task<Unit> Handle(DidChangeTextDocumentParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri;

        foreach (var change in request.ContentChanges)
        {
            // For full sync, just replace the content
            _documentContents[uri] = change.Text;
        }

        if (_documentContents.TryGetValue(uri, out var content))
        {
            // Update workspace immediately (for completions/hover to work)
            var path = uri.GetFileSystemPath();
            _workspace.UpdateDocument(path, content);

            // Debounce diagnostics - cancel any pending update for this document
            if (_diagnosticDebounce.TryGetValue(uri, out var existingCts))
            {
                await existingCts.CancelAsync();
                existingCts.Dispose();
            }

            // Schedule new diagnostic update after delay
            var cts = new CancellationTokenSource();
            _diagnosticDebounce[uri] = cts;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(_diagnosticDelayMs, cts.Token);

                    // Only publish if not cancelled
                    if (!cts.Token.IsCancellationRequested)
                    {
                        PublishDiagnostics(uri, content);
                    }
                }
                catch (TaskCanceledException)
                {
                    // Expected when user types again before delay expires
                }
            }, cts.Token);
        }

        return Unit.Value;
    }

    public override Task<Unit> Handle(DidSaveTextDocumentParams request, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Document saved: {Uri}", request.TextDocument.Uri);
        return Unit.Task;
    }

    public override async Task<Unit> Handle(DidCloseTextDocumentParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri;
        _logger.LogDebug("Document closed: {Uri}", uri);
        _documentContents.Remove(uri);

        // Clean up any pending diagnostic update
        if (_diagnosticDebounce.TryGetValue(uri, out var cts))
        {
            await cts.CancelAsync();
            cts.Dispose();
            _diagnosticDebounce.Remove(uri);
        }

        return Unit.Value;
    }

    public string? GetDocumentContent(DocumentUri uri)
    {
        return _documentContents.TryGetValue(uri, out var content) ? content : null;
    }

    /// <summary>
    /// Publish diagnostics for a document. Called after debounce delay.
    /// </summary>
    private void PublishDiagnostics(DocumentUri uri, string content)
    {
        var path = uri.GetFileSystemPath();

        try
        {
            // Re-parse to get fresh diagnostics
            var result = _workspace.UpdateDocumentWithDiagnostics(path, content);
            var allDiagnostics = new List<Diagnostic>(result.Diagnostics);

            // Run semantic validation if we have a parsed shader
            if (result.Shader != null && !result.IsPartial)
            {
                var semanticDiagnostics = _semanticValidator.Validate(result.Shader, content);
                allDiagnostics.AddRange(semanticDiagnostics);
            }

            // Publish diagnostics
            _server.TextDocument.PublishDiagnostics(new PublishDiagnosticsParams
            {
                Uri = uri,
                Diagnostics = allDiagnostics
            });

            if (allDiagnostics.Count > 0)
            {
                _logger.LogDebug("Published {Count} diagnostics for {Uri}", allDiagnostics.Count, uri);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error publishing diagnostics for {Uri}", uri);

            // Publish exception as diagnostic
            _server.TextDocument.PublishDiagnostics(new PublishDiagnosticsParams
            {
                Uri = uri,
                Diagnostics = new List<Diagnostic>
                {
                    new Diagnostic
                    {
                        Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(0, 0, 0, 1),
                        Severity = DiagnosticSeverity.Error,
                        Source = "sdsl",
                        Message = $"Parse error: {ex.Message}"
                    }
                }
            });
        }
    }

    protected override TextDocumentSyncRegistrationOptions CreateRegistrationOptions(
        TextSynchronizationCapability capability,
        ClientCapabilities clientCapabilities)
    {
        return new TextDocumentSyncRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForPattern("**/*.sdsl"),
            Change = TextDocumentSyncKind.Full,
            Save = new SaveOptions { IncludeText = true }
        };
    }

    /// <summary>
    /// Clean up all pending diagnostic tasks on shutdown.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _logger.LogDebug("Disposing TextDocumentSyncHandler, cancelling {Count} pending diagnostic tasks", _diagnosticDebounce.Count);

        foreach (var cts in _diagnosticDebounce.Values)
        {
            try
            {
                cts.Cancel();
                cts.Dispose();
            }
            catch
            {
                // Ignore disposal errors during shutdown
            }
        }
        _diagnosticDebounce.Clear();
        _documentContents.Clear();
    }
}
