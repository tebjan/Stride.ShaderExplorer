using MediatR;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Workspace;
using StrideShaderLanguageServer.Services;

namespace StrideShaderLanguageServer.Handlers;

/// <summary>
/// Handles file system change notifications from the client.
/// This enables the language server to track file renames, deletions, and creations.
/// </summary>
public class DidChangeWatchedFilesHandler : IDidChangeWatchedFilesHandler
{
    private readonly ILogger<DidChangeWatchedFilesHandler> _logger;
    private readonly ShaderWorkspace _workspace;

    public DidChangeWatchedFilesHandler(ILogger<DidChangeWatchedFilesHandler> logger, ShaderWorkspace workspace)
    {
        _logger = logger;
        _workspace = workspace;
    }

    public Task<Unit> Handle(DidChangeWatchedFilesParams request, CancellationToken cancellationToken)
    {
        foreach (var change in request.Changes)
        {
            var path = change.Uri.GetFileSystemPath();

            // Only process .sdsl files
            if (!path.EndsWith(".sdsl", StringComparison.OrdinalIgnoreCase))
                continue;

            switch (change.Type)
            {
                case FileChangeType.Created:
                    _logger.LogInformation("File created: {Path}", path);
                    _workspace.HandleFileCreated(path);
                    break;

                case FileChangeType.Deleted:
                    _logger.LogInformation("File deleted: {Path}", path);
                    _workspace.HandleFileDeleted(path);
                    break;

                case FileChangeType.Changed:
                    // File content changes are handled by TextDocumentSyncHandler
                    // This is for metadata changes, which we can ignore
                    _logger.LogDebug("File changed: {Path}", path);
                    break;
            }
        }

        return Unit.Task;
    }

    public DidChangeWatchedFilesRegistrationOptions GetRegistrationOptions(
        DidChangeWatchedFilesCapability capability,
        ClientCapabilities clientCapabilities)
    {
        return new DidChangeWatchedFilesRegistrationOptions
        {
            Watchers = new Container<OmniSharp.Extensions.LanguageServer.Protocol.Models.FileSystemWatcher>(
                new OmniSharp.Extensions.LanguageServer.Protocol.Models.FileSystemWatcher
                {
                    GlobPattern = "**/*.sdsl",
                    Kind = WatchKind.Create | WatchKind.Delete | WatchKind.Change
                }
            )
        };
    }
}
