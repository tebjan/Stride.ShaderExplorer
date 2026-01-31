using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using StrideShaderLanguageServer.Services;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace StrideShaderLanguageServer.Handlers;

/// <summary>
/// Provides code actions (quick fixes) for SDSL shaders.
/// Main feature: Suggest adding base shaders when a stream/method is not found.
/// </summary>
public class CodeActionHandler : CodeActionHandlerBase
{
    private readonly ILogger<CodeActionHandler> _logger;
    private readonly ShaderWorkspace _workspace;
    private readonly InheritanceResolver _inheritanceResolver;
    private readonly TextDocumentSyncHandler _syncHandler;

    // Regex to find the shader declaration line: "shader Name : Base1, Base2 {"
    private static readonly Regex ShaderDeclRegex = new(
        @"^(\s*shader\s+\w+\s*)(:?\s*)([\w\s,<>]*?)(\s*\{)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    public CodeActionHandler(
        ILogger<CodeActionHandler> logger,
        ShaderWorkspace workspace,
        InheritanceResolver inheritanceResolver,
        TextDocumentSyncHandler syncHandler)
    {
        _logger = logger;
        _workspace = workspace;
        _inheritanceResolver = inheritanceResolver;
        _syncHandler = syncHandler;
    }

    public override Task<CommandOrCodeActionContainer?> Handle(CodeActionParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri;
        var range = request.Range;
        var diagnostics = request.Context.Diagnostics;

        _logger.LogDebug("Code action requested at {Uri} for range {Range}", uri, range);

        var content = _syncHandler.GetDocumentContent(uri);
        if (string.IsNullOrEmpty(content))
        {
            return Task.FromResult<CommandOrCodeActionContainer?>(null);
        }

        var path = uri.GetFileSystemPath();
        var shaderName = Path.GetFileNameWithoutExtension(path);
        var currentShader = _workspace.GetParsedShaderClosest(shaderName, path);

        var actions = new List<CommandOrCodeAction>();

        // Process diagnostics that we can provide fixes for
        foreach (var diagnostic in diagnostics)
        {
            if (diagnostic.Source != "sdsl") continue;

            var message = diagnostic.Message ?? "";

            // Check for "X is not defined" errors
            if (message.Contains("is not defined"))
            {
                var match = Regex.Match(message, @"'(\w+)' is not defined");
                if (match.Success)
                {
                    var undefinedName = match.Groups[1].Value;
                    var fixActions = CreateAddBaseShaderActions(
                        uri, content, undefinedName, diagnostic.Range, currentShader, path);
                    actions.AddRange(fixActions);
                }
            }
            // Check for filename/shader name mismatch
            else if (diagnostic.Code?.String == "shader-filename-mismatch" ||
                     message.Contains("doesn't match shader name"))
            {
                // Check if this is a copy pattern (stored in diagnostic Data)
                var isCopyPattern = diagnostic.Data?.ToString() == "copy";
                var fixActions = CreateFilenameMismatchActions(uri, content, path, message, diagnostic.Range, isCopyPattern);
                actions.AddRange(fixActions);
            }
            // Check for base shader not found
            else if (message.Contains("not found in workspace"))
            {
                // This is already about a missing base shader - no fix available
                // unless we want to offer creating it
            }
        }

        // Also check if the cursor is on a streams.X or method call that's not found
        // even without a diagnostic (for proactive suggestions)
        var cursorActions = CreateProactiveActions(uri, content, range, currentShader);
        actions.AddRange(cursorActions);

        if (actions.Count == 0)
        {
            return Task.FromResult<CommandOrCodeActionContainer?>(null);
        }

        return Task.FromResult<CommandOrCodeActionContainer?>(new CommandOrCodeActionContainer(actions));
    }

    /// <summary>
    /// Create code actions to add a base shader that defines the missing identifier.
    /// </summary>
    private List<CommandOrCodeAction> CreateAddBaseShaderActions(
        DocumentUri uri,
        string content,
        string undefinedName,
        Range diagnosticRange,
        ParsedShader? currentShader,
        string? contextFilePath = null)
    {
        var actions = new List<CommandOrCodeAction>();

        // Find shaders that define this name
        var shadersWithVariable = _inheritanceResolver.FindShadersDefiningVariable(undefinedName);
        var shadersWithStream = _inheritanceResolver.FindShadersDefiningStream(undefinedName);
        var shadersWithMethod = _inheritanceResolver.FindShadersDefiningMethod(undefinedName);

        // Combine and deduplicate, prefer streams for stream-like names
        var candidateShaders = new HashSet<string>();

        // Streams take priority (most common case)
        foreach (var s in shadersWithStream) candidateShaders.Add(s);
        foreach (var s in shadersWithVariable) candidateShaders.Add(s);
        foreach (var s in shadersWithMethod) candidateShaders.Add(s);

        // Filter out shaders that are already inherited - use context-aware resolution
        if (currentShader != null)
        {
            var alreadyInherited = new HashSet<string>(currentShader.BaseShaderNames);
            var inheritanceChain = _inheritanceResolver.ResolveInheritanceChain(currentShader.Name, contextFilePath);
            foreach (var baseShader in inheritanceChain)
            {
                alreadyInherited.Add(baseShader.Name);
            }
            candidateShaders.RemoveWhere(s => alreadyInherited.Contains(s));
        }

        if (candidateShaders.Count == 0)
        {
            _logger.LogDebug("No candidate shaders found for undefined name '{Name}'", undefinedName);
            return actions;
        }

        _logger.LogDebug("Found {Count} candidate shaders defining '{Name}': {Shaders}",
            candidateShaders.Count, undefinedName, string.Join(", ", candidateShaders));

        // Create an action for each candidate shader
        foreach (var candidateShader in candidateShaders.Take(5)) // Limit to 5 suggestions
        {
            var edit = CreateAddBaseShaderEdit(uri, content, candidateShader);
            if (edit == null) continue;

            var action = new CodeAction
            {
                Title = $"Add base shader '{candidateShader}'",
                Kind = CodeActionKind.QuickFix,
                Diagnostics = new Container<Diagnostic>(new Diagnostic
                {
                    Range = diagnosticRange,
                    Message = $"'{undefinedName}' is not defined"
                }),
                Edit = edit,
                IsPreferred = candidateShaders.Count == 1 // Preferred if only one option
            };

            actions.Add(action);
        }

        return actions;
    }

    /// <summary>
    /// Create a WorkspaceEdit that adds a base shader to the shader declaration.
    /// </summary>
    private WorkspaceEdit? CreateAddBaseShaderEdit(DocumentUri uri, string content, string baseShaderToAdd)
    {
        // Find the shader declaration line
        var match = ShaderDeclRegex.Match(content);
        if (!match.Success)
        {
            _logger.LogWarning("Could not find shader declaration in content");
            return null;
        }

        // Groups: 1 = "shader Name ", 2 = ": " or "", 3 = existing bases, 4 = " {"
        var shaderPart = match.Groups[1].Value;      // "shader MyShader "
        var colonPart = match.Groups[2].Value;       // ": " or ""
        var basesPart = match.Groups[3].Value.Trim(); // "Base1, Base2" or ""
        var bracePart = match.Groups[4].Value;       // " {"

        string newDeclaration;
        if (string.IsNullOrEmpty(basesPart))
        {
            // No existing bases - add ": NewBase"
            newDeclaration = $"{shaderPart}: {baseShaderToAdd}{bracePart}";
        }
        else
        {
            // Has existing bases - append ", NewBase"
            newDeclaration = $"{shaderPart}: {basesPart}, {baseShaderToAdd}{bracePart}";
        }

        // Calculate the range of the match
        var lines = content.Split('\n');
        int startLine = 0, startCol = 0;
        int currentPos = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            var lineLength = lines[i].Length + 1; // +1 for newline
            if (currentPos + lineLength > match.Index)
            {
                startLine = i;
                startCol = match.Index - currentPos;
                break;
            }
            currentPos += lineLength;
        }

        // Find end position
        var endPos = match.Index + match.Length;
        int endLine = startLine, endCol = startCol + match.Length;
        currentPos = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            var lineLength = lines[i].Length + 1;
            if (currentPos + lineLength > endPos)
            {
                endLine = i;
                endCol = endPos - currentPos;
                break;
            }
            currentPos += lineLength;
        }

        var editRange = new Range(startLine, startCol, endLine, endCol);

        return new WorkspaceEdit
        {
            Changes = new Dictionary<DocumentUri, IEnumerable<TextEdit>>
            {
                [uri] = new[]
                {
                    new TextEdit
                    {
                        Range = editRange,
                        NewText = newDeclaration
                    }
                }
            }
        };
    }

    /// <summary>
    /// Create proactive actions when cursor is on a potentially undefined identifier.
    /// </summary>
    private List<CommandOrCodeAction> CreateProactiveActions(
        DocumentUri uri,
        string content,
        Range cursorRange,
        ParsedShader? currentShader)
    {
        // This could be extended to offer suggestions even without explicit errors
        // For now, return empty - the diagnostic-based suggestions cover most cases
        return new List<CommandOrCodeAction>();
    }

    /// <summary>
    /// Create code actions for filename/shader name mismatch.
    /// </summary>
    private List<CommandOrCodeAction> CreateFilenameMismatchActions(
        DocumentUri uri,
        string content,
        string filePath,
        string diagnosticMessage,
        Range diagnosticRange,
        bool isCopyPattern)
    {
        var actions = new List<CommandOrCodeAction>();

        // Parse the message to extract file and shader names
        // Message format: "Filename 'X' doesn't match shader name 'Y'"
        var filenameMatch = Regex.Match(diagnosticMessage, @"Filename '([^']+)'");
        var shaderNameMatch = Regex.Match(diagnosticMessage, @"shader name '(\w+)'");

        if (!filenameMatch.Success || !shaderNameMatch.Success)
        {
            _logger.LogWarning("Could not parse filename mismatch message: {Message}", diagnosticMessage);
            return actions;
        }

        var fileName = filenameMatch.Groups[1].Value;
        var shaderName = shaderNameMatch.Groups[1].Value;

        // Action 1: Rename file to match shader name
        var directory = Path.GetDirectoryName(filePath) ?? "";
        var newFilePath = Path.Combine(directory, shaderName + ".sdsl");

        // Use a command for file rename since WorkspaceEdit.DocumentChanges with RenameFile
        // requires the client to support it, which may vary
        var renameFileAction = new CodeAction
        {
            Title = $"Rename file to '{shaderName}.sdsl'",
            Kind = CodeActionKind.QuickFix,
            Diagnostics = new Container<Diagnostic>(new Diagnostic
            {
                Range = diagnosticRange,
                Message = diagnosticMessage,
                Code = "shader-filename-mismatch"
            }),
            Command = new Command
            {
                Name = "strideShaderTools.renameFile",
                Title = $"Rename file to '{shaderName}.sdsl'",
                Arguments = new Newtonsoft.Json.Linq.JArray { filePath, newFilePath }
            },
            IsPreferred = true // File rename is usually the preferred action
        };
        actions.Add(renameFileAction);

        // Action 2: Rename shader to match filename (only if not a copy pattern)
        if (!isCopyPattern)
        {
            var renameShaderEdit = CreateRenameShaderEdit(uri, content, shaderName, fileName);
            if (renameShaderEdit != null)
            {
                var renameShaderAction = new CodeAction
                {
                    Title = $"Rename shader to '{fileName}'",
                    Kind = CodeActionKind.QuickFix,
                    Diagnostics = new Container<Diagnostic>(new Diagnostic
                    {
                        Range = diagnosticRange,
                        Message = diagnosticMessage,
                        Code = "shader-filename-mismatch"
                    }),
                    Edit = renameShaderEdit,
                    IsPreferred = false
                };
                actions.Add(renameShaderAction);
            }
        }

        return actions;
    }

    /// <summary>
    /// Create a WorkspaceEdit that renames the shader declaration.
    /// </summary>
    private WorkspaceEdit? CreateRenameShaderEdit(DocumentUri uri, string content, string oldName, string newName)
    {
        // Find "shader OldName" in the content and replace with "shader NewName"
        var pattern = $@"(\bshader\s+){Regex.Escape(oldName)}(\s*[:<{{])";
        var match = Regex.Match(content, pattern);

        if (!match.Success)
        {
            _logger.LogWarning("Could not find shader declaration for '{OldName}'", oldName);
            return null;
        }

        // Calculate start position of the shader name (after "shader ")
        var shaderKeywordEnd = match.Groups[1].Index + match.Groups[1].Length;
        var nameStart = shaderKeywordEnd;
        var nameEnd = nameStart + oldName.Length;

        // Convert byte positions to line/column
        var lines = content.Split('\n');
        int startLine = 0, startCol = 0, endLine = 0, endCol = 0;
        int currentPos = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            var lineLength = lines[i].Length + 1; // +1 for newline

            if (currentPos + lineLength > nameStart && startLine == 0 && startCol == 0)
            {
                startLine = i;
                startCol = nameStart - currentPos;
            }

            if (currentPos + lineLength > nameEnd)
            {
                endLine = i;
                endCol = nameEnd - currentPos;
                break;
            }

            currentPos += lineLength;
        }

        var editRange = new Range(startLine, startCol, endLine, endCol);

        return new WorkspaceEdit
        {
            Changes = new Dictionary<DocumentUri, IEnumerable<TextEdit>>
            {
                [uri] = new[]
                {
                    new TextEdit
                    {
                        Range = editRange,
                        NewText = newName
                    }
                }
            }
        };
    }

    public override Task<CodeAction> Handle(CodeAction request, CancellationToken cancellationToken)
    {
        // No resolution needed - we provide full edit in the initial response
        return Task.FromResult(request);
    }

    protected override CodeActionRegistrationOptions CreateRegistrationOptions(
        CodeActionCapability capability,
        ClientCapabilities clientCapabilities)
    {
        return new CodeActionRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForPattern("**/*.sdsl"),
            CodeActionKinds = new Container<CodeActionKind>(CodeActionKind.QuickFix),
            ResolveProvider = false
        };
    }
}
