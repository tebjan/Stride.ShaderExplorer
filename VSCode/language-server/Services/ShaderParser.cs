using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Stride.Core.Shaders.Ast;
using Stride.Core.Shaders.Ast.Hlsl;
using Stride.Core.Shaders.Ast.Stride;
using Stride.Shaders.Parser;
using ShaderMacro = Stride.Core.Shaders.Parser.ShaderMacro;
using SourceSpan = Stride.Core.Shaders.Ast.SourceSpan;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace StrideShaderLanguageServer.Services;

// Extension method for Shader AST
internal static class ShaderExtensions
{
    public static ClassType? GetFirstClassDecl(this Shader shader)
    {
        var result = shader.Declarations.OfType<ClassType>().FirstOrDefault();
        if (result == null)
        {
            var nameSpace = shader.Declarations.OfType<NamespaceBlock>().FirstOrDefault();
            if (nameSpace != null)
            {
                result = nameSpace.Body.OfType<ClassType>().FirstOrDefault();
            }
        }
        return result;
    }
}

/// <summary>
/// Result of parsing a shader, including diagnostics and partial results.
/// </summary>
public class ShaderParseResult
{
    public ParsedShader? Shader { get; set; }
    public List<Diagnostic> Diagnostics { get; set; } = new();
    public bool HasErrors => Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
    public bool IsPartial { get; set; }
}

/// <summary>
/// Parses SDSL shader files using Stride's shader parser.
/// </summary>
public class ShaderParser
{
    private readonly ILogger<ShaderParser> _logger;
    private readonly Dictionary<string, ParsedShader> _cache = new();
    private readonly object _cacheLock = new();

    // Regex patterns for fallback parsing
    private static readonly Regex ShaderDeclRegex = new(
        @"shader\s+(\w+)\s*(?::\s*([\w\s,<>]+?))?(?:\s*\{|$)",
        RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex VariableDeclRegex = new(
        @"(?:stage\s+|stream\s+|compose\s+)*(\w+(?:<[^>]+>)?)\s+(\w+)\s*[;=]",
        RegexOptions.Compiled);
    private static readonly Regex MethodDeclRegex = new(
        @"(?:override\s+|abstract\s+|stage\s+)*(\w+(?:<[^>]+>)?)\s+(\w+)\s*\([^)]*\)",
        RegexOptions.Compiled);

    private static readonly ShaderMacro[] Macros = new[]
    {
        new ShaderMacro("class", "shader")
    };

    public ShaderParser(ILogger<ShaderParser> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Parse shader and return result with diagnostics.
    /// Always returns diagnostics, and attempts to extract partial AST even on error.
    /// </summary>
    public ShaderParseResult TryParseWithDiagnostics(string shaderName, string sourceCode)
    {
        var result = new ShaderParseResult();
        var sourceLines = sourceCode.Split('\n');

        try
        {
            var inputFileName = shaderName + ".sdsl";
            var parsingResult = StrideShaderParser.TryPreProcessAndParse(sourceCode, inputFileName, Macros);

            // Convert parser messages to LSP diagnostics (deduplicated)
            var seenDiagnostics = new HashSet<string>();
            foreach (var msg in parsingResult.Messages)
            {
                var diagnostic = CreateDiagnosticFromMessage(msg, sourceLines);

                // Create a key for deduplication (line:col:message)
                var key = $"{diagnostic.Range.Start.Line}:{diagnostic.Range.Start.Character}:{diagnostic.Message}";
                if (seenDiagnostics.Add(key))
                {
                    result.Diagnostics.Add(diagnostic);
                }
            }

            // Try to extract AST even with errors - Stride's Irony parser may provide partial results
            var shaderClass = parsingResult.Shader?.GetFirstClassDecl();
            if (shaderClass != null)
            {
                result.Shader = new ParsedShader(shaderName, parsingResult.Shader!, shaderClass);
                result.IsPartial = parsingResult.HasErrors;

                if (parsingResult.HasErrors)
                {
                    _logger.LogDebug("Extracted partial AST for {ShaderName} despite errors", shaderName);
                }
            }
            else if (parsingResult.HasErrors)
            {
                // Fallback: try regex extraction for basic structure
                result.Shader = TryExtractShaderStructure(shaderName, sourceCode);
                result.IsPartial = result.Shader != null;

                if (result.Shader != null)
                {
                    _logger.LogDebug("Extracted shader structure via regex fallback for {ShaderName}", shaderName);
                }
            }

            if (result.Shader == null && !parsingResult.HasErrors)
            {
                _logger.LogWarning("No shader class found in {ShaderName}", shaderName);
                result.Diagnostics.Add(new Diagnostic
                {
                    Range = new Range(0, 0, 0, 1),
                    Severity = DiagnosticSeverity.Error,
                    Source = "sdsl",
                    Message = "No shader class declaration found"
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception while parsing shader {ShaderName}", shaderName);
            result.Diagnostics.Add(new Diagnostic
            {
                Range = new Range(0, 0, 0, 1),
                Severity = DiagnosticSeverity.Error,
                Source = "sdsl",
                Message = $"Parse exception: {ex.Message}"
            });

            // Try regex fallback even on exception
            result.Shader = TryExtractShaderStructure(shaderName, sourceCode);
            result.IsPartial = result.Shader != null;
        }

        return result;
    }

    /// <summary>
    /// Legacy method - parses and caches result. Returns null on error.
    /// </summary>
    public ParsedShader? TryParse(string shaderName, string sourceCode)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(shaderName, out var cached))
            {
                return cached;
            }

            var result = TryParseWithDiagnostics(shaderName, sourceCode);

            // Only cache successful full parses (not partial)
            if (result.Shader != null && !result.IsPartial)
            {
                _cache[shaderName] = result.Shader;
                _logger.LogDebug("Successfully parsed and cached shader {ShaderName}", shaderName);
            }

            return result.Shader;
        }
    }

    /// <summary>
    /// Create LSP diagnostic from Stride parser message.
    /// Uses dynamic access since the exact type varies between Stride versions.
    /// </summary>
    private static Diagnostic CreateDiagnosticFromMessage(dynamic msg, string[] sourceLines)
    {
        try
        {
            // Stride uses 1-based line/column, LSP uses 0-based
            int line = 0, col = 0, endCol = 1;
            string text = "";
            string code = "";
            DiagnosticSeverity severity = DiagnosticSeverity.Error;

            // Try to extract span info
            try
            {
                var span = msg.Span;
                line = Math.Max(0, (int)span.Location.Line - 1);
                col = Math.Max(0, (int)span.Location.Column - 1);
                endCol = col + Math.Max(1, (int)span.Length);
            }
            catch { /* Span info not available */ }

            // Try to extract message text
            try { text = msg.Text ?? ""; } catch { }

            // Try to extract code
            try { code = msg.Code ?? ""; } catch { }

            // Try to extract severity level
            try
            {
                var levelStr = msg.Level.ToString();
                if (levelStr.Contains("Error"))
                    severity = DiagnosticSeverity.Error;
                else if (levelStr.Contains("Warning"))
                    severity = DiagnosticSeverity.Warning;
                else
                    severity = DiagnosticSeverity.Information;
            }
            catch { }

            // Improve the error message
            var friendlyMessage = ImproveErrorMessage(text);

            // Adjust error position for certain errors
            (line, col, endCol) = AdjustErrorPosition(line, col, endCol, text, friendlyMessage, sourceLines);

            return new Diagnostic
            {
                Range = new Range(line, col, line, endCol),
                Severity = severity,
                Source = "sdsl",
                Message = string.IsNullOrEmpty(friendlyMessage) ? "Parse error" : friendlyMessage
            };
        }
        catch
        {
            // Fallback if dynamic access fails entirely
            return new Diagnostic
            {
                Range = new Range(0, 0, 0, 1),
                Severity = DiagnosticSeverity.Error,
                Source = "sdsl",
                Message = msg?.ToString() ?? "Parse error"
            };
        }
    }

    /// <summary>
    /// Adjust error position to be more accurate.
    /// The parser often reports errors at the start of the next valid token,
    /// but the actual problem is usually at the end of the previous line.
    /// </summary>
    private static (int line, int col, int endCol) AdjustErrorPosition(
        int line, int col, int endCol, string originalMessage, string friendlyMessage, string[] sourceLines)
    {
        var lowerOriginal = originalMessage.ToLowerInvariant();
        var lowerFriendly = friendlyMessage.ToLowerInvariant();

        // Check if previous line looks incomplete (doesn't end with a statement terminator)
        bool IsPreviousLineIncomplete(int currentLine)
        {
            if (currentLine <= 0) return false;

            var prevLine = currentLine - 1;
            // Skip empty/whitespace-only lines and comments
            while (prevLine >= 0)
            {
                var content = GetLineContent(sourceLines, prevLine).Trim();
                if (string.IsNullOrEmpty(content))
                {
                    prevLine--;
                    continue;
                }
                // Skip comment-only lines
                if (content.StartsWith("//"))
                {
                    prevLine--;
                    continue;
                }

                // Check if line ends with a statement terminator
                var trimmed = content.TrimEnd();
                if (trimmed.EndsWith(";") || trimmed.EndsWith("{") || trimmed.EndsWith("}") ||
                    trimmed.EndsWith(",") || trimmed.EndsWith(":") || trimmed.EndsWith("*/"))
                    return false;

                // Line doesn't end with terminator - likely incomplete
                return true;
            }
            return false;
        }

        // For errors that suggest missing semicolon or are generic syntax errors,
        // check if the previous line looks incomplete
        bool shouldCheckPrevLine = lowerFriendly.Contains("semicolon") ||
                                   lowerFriendly.Contains("missing") ||
                                   lowerFriendly.Contains("unexpected") ||
                                   lowerOriginal.Contains(";") ||
                                   lowerOriginal.Contains("expected");

        if (shouldCheckPrevLine && line > 0 && IsPreviousLineIncomplete(line))
        {
            // Find the previous non-empty, non-comment line
            var prevLine = line - 1;
            while (prevLine >= 0)
            {
                var content = GetLineContent(sourceLines, prevLine).Trim();
                if (!string.IsNullOrEmpty(content) && !content.StartsWith("//"))
                    break;
                prevLine--;
            }

            if (prevLine >= 0)
            {
                var prevLineContent = GetLineContent(sourceLines, prevLine);
                var endOfPrevLine = prevLineContent.TrimEnd().Length;
                return (prevLine, Math.Max(0, endOfPrevLine - 1), endOfPrevLine);
            }
        }

        // For errors where column is at the very end or beyond the line, clamp to line length
        if (line >= 0 && line < sourceLines.Length)
        {
            var lineContent = GetLineContent(sourceLines, line);
            var lineLength = lineContent.Length;
            if (col >= lineLength)
            {
                col = Math.Max(0, lineLength - 1);
                endCol = lineLength;
            }
            endCol = Math.Min(endCol, lineLength);
        }

        return (line, col, endCol);
    }

    private static string GetLineContent(string[] lines, int lineIndex)
    {
        if (lineIndex < 0 || lineIndex >= lines.Length)
            return "";
        return lines[lineIndex].TrimEnd('\r');
    }

    /// <summary>
    /// Translate verbose parser errors to user-friendly messages.
    /// </summary>
    private static string ImproveErrorMessage(string originalMessage)
    {
        if (string.IsNullOrEmpty(originalMessage))
            return originalMessage;

        // Common patterns and their friendly replacements
        // Pattern: "Syntax error, expected: X"
        if (originalMessage.StartsWith("Syntax error", StringComparison.OrdinalIgnoreCase))
        {
            // Extract what was expected
            var expectedMatch = Regex.Match(originalMessage, @"expected:?\s*(.+)$", RegexOptions.IgnoreCase);
            if (expectedMatch.Success)
            {
                var expected = expectedMatch.Groups[1].Value.Trim();
                return SimplifyExpectedList(expected);
            }
        }

        // Pattern: "Invalid character: X"
        if (originalMessage.Contains("Invalid character", StringComparison.OrdinalIgnoreCase))
        {
            var charMatch = Regex.Match(originalMessage, @"Invalid character:?\s*'?(.)'?", RegexOptions.IgnoreCase);
            if (charMatch.Success)
            {
                return $"Unexpected character '{charMatch.Groups[1].Value}'";
            }
        }

        // Pattern: "Unexpected token: X"
        if (originalMessage.Contains("Unexpected token", StringComparison.OrdinalIgnoreCase))
        {
            var tokenMatch = Regex.Match(originalMessage, @"Unexpected token:?\s*(.+)$", RegexOptions.IgnoreCase);
            if (tokenMatch.Success)
            {
                return $"Unexpected '{tokenMatch.Groups[1].Value.Trim()}'";
            }
        }

        // Pattern: Contains long list of keywords/terminals
        if (originalMessage.Length > 80 && originalMessage.Contains(","))
        {
            // Likely a long "expected one of: ..." message
            return SimplifyLongExpectedMessage(originalMessage);
        }

        return originalMessage;
    }

    /// <summary>
    /// Simplify a list of expected tokens to a more readable message.
    /// Phrased as questions/suggestions since we can't be 100% sure of the fix.
    /// </summary>
    private static string SimplifyExpectedList(string expected)
    {
        var lower = expected.ToLowerInvariant();

        // Check for common patterns - phrase as questions/suggestions
        if (lower.Contains(";") || lower.Contains("semicolon"))
            return "Missing semicolon?";

        if (lower.Contains("{") || lower.Contains("lbrace"))
            return "Missing opening brace '{'?";

        if (lower.Contains("}") || lower.Contains("rbrace"))
            return "Missing closing brace '}'?";

        if (lower.Contains("(") || lower.Contains("lparen"))
            return "Missing opening parenthesis '('?";

        if (lower.Contains(")") || lower.Contains("rparen"))
            return "Missing closing parenthesis ')'?";

        if (lower.Contains("[") || lower.Contains("lbracket"))
            return "Missing opening bracket '['?";

        if (lower.Contains("]") || lower.Contains("rbracket"))
            return "Missing closing bracket ']'?";

        if (lower.Contains("identifier"))
            return "Expected an identifier";

        if (lower.Contains("type") && !lower.Contains("typedef"))
            return "Expected a type";

        if (lower.Contains("expression") || lower.Contains("expr"))
            return "Expected an expression";

        if (lower.Contains("statement"))
            return "Expected a statement";

        // If the expected list is short enough, show it directly
        if (expected.Length <= 30)
            return $"Expected {expected}";

        // Otherwise just show a generic message
        return "Syntax error";
    }

    /// <summary>
    /// Simplify very long "expected one of: ..." messages.
    /// </summary>
    private static string SimplifyLongExpectedMessage(string message)
    {
        var lower = message.ToLowerInvariant();

        // Try to determine context from what's in the list
        if (lower.Contains("identifier") && lower.Contains("("))
        {
            if (lower.Contains("override") || lower.Contains("stage"))
                return "Unexpected code here - missing declaration?";
            return "Unexpected code - missing semicolon or declaration?";
        }

        if (lower.Contains(";") && (lower.Contains("=") || lower.Contains("initializer")))
            return "Statement incomplete - missing semicolon or value?";

        if (lower.Contains("shader") || lower.Contains("class") || lower.Contains("struct"))
            return "Expected a type declaration";

        // Generic fallback
        return "Syntax error near here";
    }

    /// <summary>
    /// Fallback: Extract basic shader structure using regex when AST parsing fails.
    /// </summary>
    private ParsedShader? TryExtractShaderStructure(string name, string source)
    {
        var match = ShaderDeclRegex.Match(source);
        if (!match.Success)
            return null;

        var shaderName = match.Groups[1].Value;
        var basesString = match.Groups[2].Success ? match.Groups[2].Value : "";

        var baseNames = string.IsNullOrWhiteSpace(basesString)
            ? new List<string>()
            : basesString.Split(',')
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();

        // Try to extract variables and methods with regex
        var variables = new List<RegexExtractedVariable>();
        var methods = new List<RegexExtractedMethod>();

        foreach (Match varMatch in VariableDeclRegex.Matches(source))
        {
            variables.Add(new RegexExtractedVariable
            {
                TypeName = varMatch.Groups[1].Value,
                Name = varMatch.Groups[2].Value
            });
        }

        foreach (Match methodMatch in MethodDeclRegex.Matches(source))
        {
            methods.Add(new RegexExtractedMethod
            {
                ReturnType = methodMatch.Groups[1].Value,
                Name = methodMatch.Groups[2].Value
            });
        }

        _logger.LogDebug("Regex fallback extracted: {VarCount} variables, {MethodCount} methods",
            variables.Count, methods.Count);

        return ParsedShader.CreatePartial(name, baseNames, variables, methods);
    }

    public void InvalidateCache(string? shaderName = null)
    {
        lock (_cacheLock)
        {
            if (shaderName != null)
            {
                _cache.Remove(shaderName);
            }
            else
            {
                _cache.Clear();
            }
        }
    }

    public IReadOnlyCollection<string> GetCachedShaderNames()
    {
        lock (_cacheLock)
        {
            return _cache.Keys.ToList();
        }
    }
}

/// <summary>
/// Regex-extracted variable for fallback parsing.
/// </summary>
public class RegexExtractedVariable
{
    public string Name { get; set; } = "";
    public string TypeName { get; set; } = "";
}

/// <summary>
/// Regex-extracted method for fallback parsing.
/// </summary>
public class RegexExtractedMethod
{
    public string Name { get; set; } = "";
    public string ReturnType { get; set; } = "";
}

/// <summary>
/// Represents a parsed SDSL shader with its AST and extracted information.
/// </summary>
public class ParsedShader
{
    public string Name { get; }
    public Shader? Shader { get; }
    public ClassType? ShaderClass { get; }
    public bool IsPartial { get; }

    public IReadOnlyList<string> BaseShaderNames { get; }
    public IReadOnlyList<ShaderVariable> Variables { get; }
    public IReadOnlyList<ShaderMethod> Methods { get; }
    public IReadOnlyList<ShaderComposition> Compositions { get; }

    public ParsedShader(string name, Shader shader, ClassType shaderClass)
    {
        Name = name;
        Shader = shader;
        ShaderClass = shaderClass;
        IsPartial = false;

        // Extract base shader names
        BaseShaderNames = shaderClass.BaseClasses?
            .Select(bc => bc.Name.Text)
            .ToList() ?? new List<string>();

        // Extract variables
        var variables = shaderClass.Members.OfType<Variable>().ToList();
        Variables = variables.Select(v => new ShaderVariable(v)).ToList();

        // Extract methods
        Methods = shaderClass.Members
            .OfType<MethodDeclaration>()
            .Select(m => new ShaderMethod(m))
            .ToList();

        // Extract compositions (variables with 'compose' qualifier)
        Compositions = variables
            .Where(v => v.Qualifiers.Contains(StrideStorageQualifier.Compose))
            .Select(v => new ShaderComposition(v))
            .ToList();
    }

    // Private constructor for partial shaders (from regex fallback)
    private ParsedShader(
        string name,
        IReadOnlyList<string> baseShaderNames,
        IReadOnlyList<ShaderVariable> variables,
        IReadOnlyList<ShaderMethod> methods)
    {
        Name = name;
        Shader = null;
        ShaderClass = null;
        IsPartial = true;
        BaseShaderNames = baseShaderNames;
        Variables = variables;
        Methods = methods;
        Compositions = new List<ShaderComposition>();
    }

    /// <summary>
    /// Create a partial shader from regex-extracted information.
    /// </summary>
    public static ParsedShader CreatePartial(
        string name,
        List<string> baseNames,
        List<RegexExtractedVariable> variables,
        List<RegexExtractedMethod> methods)
    {
        return new ParsedShader(
            name,
            baseNames,
            variables.Select(v => ShaderVariable.CreatePartial(v.Name, v.TypeName)).ToList(),
            methods.Select(m => ShaderMethod.CreatePartial(m.Name, m.ReturnType)).ToList()
        );
    }
}

public class ShaderVariable
{
    public string Name { get; }
    public string TypeName { get; }
    public bool IsStage { get; }
    public bool IsStream { get; }
    public bool IsCompose { get; }
    public SourceSpan Location { get; }

    public ShaderVariable(Variable variable)
    {
        Name = variable.Name.Text;
        TypeName = variable.Type?.Name?.Text ?? "unknown";
        IsStage = variable.Qualifiers.Contains(StrideStorageQualifier.Stage);
        IsStream = variable.Qualifiers.Contains(StrideStorageQualifier.Stream);
        IsCompose = variable.Qualifiers.Contains(StrideStorageQualifier.Compose);
        Location = variable.Span;
    }

    // Private constructor for partial variables
    private ShaderVariable(string name, string typeName)
    {
        Name = name;
        TypeName = typeName;
        IsStage = false;
        IsStream = false;
        IsCompose = false;
        Location = new SourceSpan();
    }

    public static ShaderVariable CreatePartial(string name, string typeName)
    {
        return new ShaderVariable(name, typeName);
    }
}

public class ShaderMethod
{
    public string Name { get; }
    public string ReturnType { get; }
    public IReadOnlyList<ShaderParameter> Parameters { get; }
    public bool IsOverride { get; }
    public bool IsAbstract { get; }
    public bool IsStage { get; }
    public SourceSpan Location { get; }

    public ShaderMethod(MethodDeclaration method)
    {
        Name = method.Name.Text;
        ReturnType = method.ReturnType?.Name?.Text ?? "void";
        Parameters = method.Parameters
            .Select(p => new ShaderParameter(p.Name.Text, p.Type?.Name?.Text ?? "unknown"))
            .ToList();
        // Check for override/abstract in qualifiers text representation
        var qualText = method.Qualifiers.ToString();
        IsOverride = qualText.Contains("override");
        IsAbstract = qualText.Contains("abstract");
        IsStage = method.Qualifiers.Contains(StrideStorageQualifier.Stage);
        Location = method.Span;
    }

    // Private constructor for partial methods
    private ShaderMethod(string name, string returnType)
    {
        Name = name;
        ReturnType = returnType;
        Parameters = new List<ShaderParameter>();
        IsOverride = false;
        IsAbstract = false;
        IsStage = false;
        Location = new SourceSpan();
    }

    public static ShaderMethod CreatePartial(string name, string returnType)
    {
        return new ShaderMethod(name, returnType);
    }
}

public class ShaderParameter
{
    public string Name { get; }
    public string TypeName { get; }

    public ShaderParameter(string name, string typeName)
    {
        Name = name;
        TypeName = typeName;
    }
}

public class ShaderComposition
{
    public string Name { get; }
    public string TypeName { get; }
    public SourceSpan Location { get; }

    public ShaderComposition(Variable variable)
    {
        Name = variable.Name.Text;
        TypeName = variable.Type?.Name?.Text ?? "unknown";
        Location = variable.Span;
    }
}
