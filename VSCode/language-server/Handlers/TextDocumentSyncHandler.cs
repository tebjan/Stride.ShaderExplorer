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

public class TextDocumentSyncHandler : TextDocumentSyncHandlerBase
{
    private readonly ILogger<TextDocumentSyncHandler> _logger;
    private readonly ShaderWorkspace _workspace;
    private readonly ShaderParser _parser;
    private readonly ILanguageServerFacade _server;
    private readonly Dictionary<DocumentUri, string> _documentContents = new();

    public TextDocumentSyncHandler(
        ILogger<TextDocumentSyncHandler> logger,
        ShaderWorkspace workspace,
        ShaderParser parser,
        ILanguageServerFacade server)
    {
        _logger = logger;
        _workspace = workspace;
        _parser = parser;
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

        // Update workspace and publish diagnostics
        UpdateDocumentAndPublishDiagnostics(uri, content);

        return Unit.Task;
    }

    public override Task<Unit> Handle(DidChangeTextDocumentParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri;

        foreach (var change in request.ContentChanges)
        {
            // For full sync, just replace the content
            _documentContents[uri] = change.Text;
        }

        if (_documentContents.TryGetValue(uri, out var content))
        {
            UpdateDocumentAndPublishDiagnostics(uri, content);
        }

        return Unit.Task;
    }

    public override Task<Unit> Handle(DidSaveTextDocumentParams request, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Document saved: {Uri}", request.TextDocument.Uri);
        return Unit.Task;
    }

    public override Task<Unit> Handle(DidCloseTextDocumentParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri;
        _logger.LogDebug("Document closed: {Uri}", uri);
        _documentContents.Remove(uri);
        return Unit.Task;
    }

    public string? GetDocumentContent(DocumentUri uri)
    {
        return _documentContents.TryGetValue(uri, out var content) ? content : null;
    }

    private void UpdateDocumentAndPublishDiagnostics(DocumentUri uri, string content)
    {
        var path = uri.GetFileSystemPath();
        var name = Path.GetFileNameWithoutExtension(path);

        _workspace.UpdateDocument(path, content);

        // Try to parse and get diagnostics
        var diagnostics = new List<Diagnostic>();

        try
        {
            var parsed = _workspace.GetParsedShader(name);
            if (parsed == null)
            {
                // Parse error - add a general diagnostic
                diagnostics.Add(new Diagnostic
                {
                    Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(0, 0, 0, 1),
                    Severity = DiagnosticSeverity.Error,
                    Source = "sdsl",
                    Message = "Failed to parse shader. Check syntax."
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing document {Uri}", uri);
        }

        _server.TextDocument.PublishDiagnostics(new PublishDiagnosticsParams
        {
            Uri = uri,
            Diagnostics = diagnostics
        });
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
}
