using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using StrideShaderLanguageServer.Services;

namespace StrideShaderLanguageServer.Handlers;

public class CompletionHandler : CompletionHandlerBase
{
    private readonly ILogger<CompletionHandler> _logger;
    private readonly CompletionService _completionService;
    private readonly TextDocumentSyncHandler _syncHandler;

    public CompletionHandler(
        ILogger<CompletionHandler> logger,
        CompletionService completionService,
        TextDocumentSyncHandler syncHandler)
    {
        _logger = logger;
        _completionService = completionService;
        _syncHandler = syncHandler;
    }

    public override Task<CompletionList> Handle(CompletionParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri;
        var position = request.Position;

        _logger.LogDebug("Completion requested at {Uri}:{Line}:{Character}",
            uri, position.Line, position.Character);

        var content = _syncHandler.GetDocumentContent(uri);
        if (string.IsNullOrEmpty(content))
        {
            _logger.LogWarning("No content found for document {Uri}", uri);
            return Task.FromResult(new CompletionList());
        }

        var path = uri.GetFileSystemPath();
        var items = _completionService.GetCompletions(path, content, position);

        return Task.FromResult(new CompletionList(items));
    }

    public override Task<CompletionItem> Handle(CompletionItem request, CancellationToken cancellationToken)
    {
        // Return the item as-is for now
        // TODO: Add documentation resolution here
        return Task.FromResult(request);
    }

    protected override CompletionRegistrationOptions CreateRegistrationOptions(
        CompletionCapability capability,
        ClientCapabilities clientCapabilities)
    {
        return new CompletionRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForPattern("**/*.sdsl"),
            TriggerCharacters = new Container<string>(".", ":", "<"),
            ResolveProvider = true
        };
    }
}
