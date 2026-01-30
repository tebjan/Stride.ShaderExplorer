using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using StrideShaderLanguageServer.Services;

namespace StrideShaderLanguageServer.Handlers;

public class DefinitionHandler : DefinitionHandlerBase
{
    private readonly ILogger<DefinitionHandler> _logger;
    private readonly ShaderWorkspace _workspace;
    private readonly InheritanceResolver _inheritanceResolver;
    private readonly TextDocumentSyncHandler _syncHandler;

    // Regex to match shader declaration: "shader Name : Base1, Base2, ..."
    private static readonly Regex ShaderDeclRegex = new(
        @"^\s*shader\s+(\w+)\s*(?::\s*(.+?))?\s*$",
        RegexOptions.Compiled);

    // Regex to match struct declaration: "struct Name {" or "struct Name\n{"
    private static readonly Regex StructDeclRegex = new(
        @"^\s*struct\s+(\w+)\s*(?:\{|$)",
        RegexOptions.Compiled);

    public DefinitionHandler(
        ILogger<DefinitionHandler> logger,
        ShaderWorkspace workspace,
        InheritanceResolver inheritanceResolver,
        TextDocumentSyncHandler syncHandler)
    {
        _logger = logger;
        _workspace = workspace;
        _inheritanceResolver = inheritanceResolver;
        _syncHandler = syncHandler;
    }

    public override Task<LocationOrLocationLinks?> Handle(DefinitionParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri;
        var position = request.Position;

        _logger.LogInformation("Definition requested at {Uri}:{Line}:{Character}",
            uri, position.Line, position.Character);

        var content = _syncHandler.GetDocumentContent(uri);
        if (string.IsNullOrEmpty(content))
        {
            return Task.FromResult<LocationOrLocationLinks?>(null);
        }

        var word = GetWordAtPosition(content, position);
        if (string.IsNullOrEmpty(word))
        {
            return Task.FromResult<LocationOrLocationLinks?>(null);
        }

        _logger.LogInformation("Definition lookup for word: '{Word}'", word);

        // Check if it's a shader name (for base shader navigation)
        var shaderInfo = _workspace.GetShaderByName(word);
        if (shaderInfo != null)
        {
            _logger.LogInformation("Found shader: {ShaderName} at {Path}", word, shaderInfo.FilePath);

            // Find the shader declaration line in the target file
            var targetLine = FindShaderDeclarationLine(shaderInfo.FilePath, word);

            var location = new Location
            {
                Uri = new Uri(shaderInfo.FilePath),
                Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range
                {
                    Start = new Position { Line = targetLine, Character = 0 },
                    End = new Position { Line = targetLine, Character = 0 }
                }
            };

            return Task.FromResult<LocationOrLocationLinks?>(new LocationOrLocationLinks(location));
        }

        // Check for struct definition in current file first
        var structLocation = FindStructInDocument(content, word);
        if (structLocation >= 0)
        {
            _logger.LogInformation("Found struct '{Word}' in current document at line {Line}", word, structLocation);
            var location = new Location
            {
                Uri = uri,
                Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range
                {
                    Start = new Position { Line = structLocation, Character = 0 },
                    End = new Position { Line = structLocation, Character = 0 }
                }
            };
            return Task.FromResult<LocationOrLocationLinks?>(new LocationOrLocationLinks(location));
        }

        // Check for member definitions (variables, methods)
        var path = uri.GetFileSystemPath();
        var currentShaderName = Path.GetFileNameWithoutExtension(path);
        var currentParsed = _workspace.GetParsedShader(currentShaderName);

        // Check for struct in inheritance chain
        if (currentParsed != null)
        {
            var structInBaseShader = FindStructInInheritanceChain(currentParsed, word);
            if (structInBaseShader.HasValue)
            {
                _logger.LogInformation("Found struct '{Word}' in base shader at {Path}:{Line}",
                    word, structInBaseShader.Value.FilePath, structInBaseShader.Value.Line);
                var location = new Location
                {
                    Uri = new Uri(structInBaseShader.Value.FilePath),
                    Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range
                    {
                        Start = new Position { Line = structInBaseShader.Value.Line, Character = 0 },
                        End = new Position { Line = structInBaseShader.Value.Line, Character = 0 }
                    }
                };
                return Task.FromResult<LocationOrLocationLinks?>(new LocationOrLocationLinks(location));
            }
        }

        if (currentParsed != null)
        {
            // Check for variable definition
            var varMatch = _inheritanceResolver.FindVariable(currentParsed, word);
            if (varMatch.Variable != null && varMatch.DefinedIn != null)
            {
                var varShaderInfo = _workspace.GetShaderByName(varMatch.DefinedIn);
                if (varShaderInfo != null)
                {
                    var location = new Location
                    {
                        Uri = new Uri(varShaderInfo.FilePath),
                        Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range
                        {
                            Start = new Position { Line = varMatch.Variable.Location.Location.Line - 1, Character = 0 },
                            End = new Position { Line = varMatch.Variable.Location.Location.Line - 1, Character = 0 }
                        }
                    };
                    return Task.FromResult<LocationOrLocationLinks?>(new LocationOrLocationLinks(location));
                }
            }

            // Check for method definition
            var methodMatches = _inheritanceResolver.FindAllMethodsWithName(currentParsed, word).ToList();
            if (methodMatches.Count > 0)
            {
                // Return location to the first (most derived) method
                var (method, definedIn) = methodMatches[0];
                var methodShaderInfo = _workspace.GetShaderByName(definedIn);
                if (methodShaderInfo != null)
                {
                    var location = new Location
                    {
                        Uri = new Uri(methodShaderInfo.FilePath),
                        Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range
                        {
                            Start = new Position { Line = method.Location.Location.Line - 1, Character = 0 },
                            End = new Position { Line = method.Location.Location.Line - 1, Character = 0 }
                        }
                    };
                    return Task.FromResult<LocationOrLocationLinks?>(new LocationOrLocationLinks(location));
                }
            }
        }

        return Task.FromResult<LocationOrLocationLinks?>(null);
    }

    /// <summary>
    /// Find a struct declaration in the given document content.
    /// Returns the 0-based line number, or -1 if not found.
    /// </summary>
    private static int FindStructInDocument(string content, string structName)
    {
        var lines = content.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var match = StructDeclRegex.Match(lines[i]);
            if (match.Success && string.Equals(match.Groups[1].Value, structName, StringComparison.Ordinal))
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Search for a struct in the inheritance chain of the current shader.
    /// </summary>
    private (string FilePath, int Line)? FindStructInInheritanceChain(ParsedShader currentParsed, string structName)
    {
        // Get the full inheritance chain
        var chain = _inheritanceResolver.ResolveInheritanceChain(currentParsed.Name);

        foreach (var baseShader in chain)
        {
            var baseInfo = _workspace.GetShaderByName(baseShader.Name);
            if (baseInfo == null) continue;

            try
            {
                var content = File.ReadAllText(baseInfo.FilePath);
                var line = FindStructInDocument(content, structName);
                if (line >= 0)
                {
                    return (baseInfo.FilePath, line);
                }
            }
            catch
            {
                // Ignore file read errors
            }
        }

        return null;
    }

    /// <summary>
    /// Find the line number where the shader is declared in the file.
    /// </summary>
    private int FindShaderDeclarationLine(string filePath, string shaderName)
    {
        try
        {
            var lines = File.ReadAllLines(filePath);
            for (int i = 0; i < lines.Length; i++)
            {
                var match = ShaderDeclRegex.Match(lines[i]);
                if (match.Success && string.Equals(match.Groups[1].Value, shaderName, StringComparison.OrdinalIgnoreCase))
                {
                    return i; // 0-based line number
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read shader file: {FilePath}", filePath);
        }

        return 0; // Default to first line
    }

    /// <summary>
    /// Get the word at the given position.
    /// </summary>
    private static string GetWordAtPosition(string content, Position position)
    {
        var lines = content.Split('\n');
        if (position.Line < 0 || position.Line >= lines.Length)
            return string.Empty;

        var line = lines[position.Line].TrimEnd('\r');
        if (position.Character < 0 || position.Character > line.Length)
            return string.Empty;

        var start = (int)position.Character;
        var end = (int)position.Character;

        while (start > 0 && (char.IsLetterOrDigit(line[start - 1]) || line[start - 1] == '_'))
            start--;

        while (end < line.Length && (char.IsLetterOrDigit(line[end]) || line[end] == '_'))
            end++;

        if (start >= end)
            return string.Empty;

        return line.Substring(start, end - start);
    }

    protected override DefinitionRegistrationOptions CreateRegistrationOptions(
        DefinitionCapability capability,
        ClientCapabilities clientCapabilities)
    {
        return new DefinitionRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForPattern("**/*.sdsl")
        };
    }
}
