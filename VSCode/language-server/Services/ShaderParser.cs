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
    // Updated to capture template parameters: shader Name<type Param> : Base
    private static readonly Regex ShaderDeclRegex = new(
        @"shader\s+(\w+)\s*(?:<([^>]+)>)?\s*(?::\s*([\w\s,.<>]+?))?(?:\s*\{|$)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    // Regex to parse template parameter declarations: "type name, type name"
    private static readonly Regex TemplateParamRegex = new(
        @"(float[234]?|int[234]?|uint[234]?|bool|Texture[123]D|TextureCube|SamplerState|Semantic|LinkType)\s+(\w+)",
        RegexOptions.Compiled);
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
                // Extract template parameters from source code using regex
                // (AST-based extraction via reflection is unreliable)
                var templateParams = ExtractTemplateParametersFromSource(sourceCode);

                result.Shader = new ParsedShader(shaderName, parsingResult.Shader!, shaderClass, templateParams);
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
    /// Extract template parameters from source code using regex.
    /// Matches "shader Name<type Param, type Param>" pattern.
    /// </summary>
    private List<TemplateParameter> ExtractTemplateParametersFromSource(string source)
    {
        var result = new List<TemplateParameter>();

        var match = ShaderDeclRegex.Match(source);
        if (!match.Success || !match.Groups[2].Success)
            return result;

        var templateParamsString = match.Groups[2].Value;
        if (string.IsNullOrWhiteSpace(templateParamsString))
            return result;

        foreach (Match paramMatch in TemplateParamRegex.Matches(templateParamsString))
        {
            result.Add(new TemplateParameter(
                paramMatch.Groups[2].Value,  // name
                paramMatch.Groups[1].Value   // type
            ));
        }

        _logger.LogDebug("Extracted {Count} template parameters from source: {Params}",
            result.Count, string.Join(", ", result.Select(p => p.ToString())));

        return result;
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

        // Group 2: Template parameters (optional): <float Intensity, int Size>
        var templateParamsString = match.Groups[2].Success ? match.Groups[2].Value : "";
        var templateParams = new List<TemplateParameter>();
        if (!string.IsNullOrWhiteSpace(templateParamsString))
        {
            foreach (Match paramMatch in TemplateParamRegex.Matches(templateParamsString))
            {
                templateParams.Add(new TemplateParameter(
                    paramMatch.Groups[2].Value,  // name
                    paramMatch.Groups[1].Value   // type
                ));
            }
        }

        // Group 3: Base shaders (optional): BaseShader<1.0f>, OtherBase
        var basesString = match.Groups[3].Success ? match.Groups[3].Value : "";

        var baseNames = string.IsNullOrWhiteSpace(basesString)
            ? new List<string>()
            : ParseBaseShaderList(basesString);

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

        _logger.LogDebug("Regex fallback extracted: {VarCount} variables, {MethodCount} methods, {TemplateCount} template params",
            variables.Count, methods.Count, templateParams.Count);

        return ParsedShader.CreatePartial(name, baseNames, variables, methods, templateParams);
    }

    /// <summary>
    /// Parse a comma-separated list of base shaders, handling template arguments.
    /// Example: "ColorModulator<1.0f>, Texturing, OtherShader<A, B>"
    /// </summary>
    private static List<string> ParseBaseShaderList(string basesString)
    {
        var result = new List<string>();
        var depth = 0;
        var current = new System.Text.StringBuilder();

        foreach (var c in basesString)
        {
            if (c == '<') depth++;
            else if (c == '>') depth--;
            else if (c == ',' && depth == 0)
            {
                var name = current.ToString().Trim();
                if (!string.IsNullOrEmpty(name))
                    result.Add(name);
                current.Clear();
                continue;
            }
            current.Append(c);
        }

        var lastName = current.ToString().Trim();
        if (!string.IsNullOrEmpty(lastName))
            result.Add(lastName);

        return result;
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
/// Represents a template parameter in a shader declaration.
/// Example: shader MyShader<float Intensity, Texture2D Tex>
/// </summary>
public class TemplateParameter
{
    public string Name { get; }
    public string TypeName { get; }

    public TemplateParameter(string name, string typeName)
    {
        Name = name;
        TypeName = typeName;
    }

    public override string ToString() => $"{TypeName} {Name}";
}

/// <summary>
/// Represents template arguments in a base shader reference.
/// Example: ColorModulator<0.5f> has BaseName="ColorModulator", Arguments=["0.5f"]
/// </summary>
public class BaseShaderReference
{
    public string FullName { get; }
    public string BaseName { get; }
    public IReadOnlyList<string> TemplateArguments { get; }
    public bool HasTemplateArguments => TemplateArguments.Count > 0;

    public BaseShaderReference(string fullName)
    {
        FullName = fullName;
        // Parse "ShaderName<arg1, arg2>" into BaseName and Arguments
        var angleIndex = fullName.IndexOf('<');
        if (angleIndex > 0 && fullName.EndsWith(">"))
        {
            BaseName = fullName.Substring(0, angleIndex);
            var argsStr = fullName.Substring(angleIndex + 1, fullName.Length - angleIndex - 2);
            TemplateArguments = ParseTemplateArguments(argsStr);
        }
        else
        {
            BaseName = fullName;
            TemplateArguments = new List<string>();
        }
    }

    private static List<string> ParseTemplateArguments(string argsStr)
    {
        // Simple parsing - split by comma, but handle nested angle brackets
        var args = new List<string>();
        var depth = 0;
        var current = new System.Text.StringBuilder();

        foreach (var c in argsStr)
        {
            if (c == '<') depth++;
            else if (c == '>') depth--;
            else if (c == ',' && depth == 0)
            {
                args.Add(current.ToString().Trim());
                current.Clear();
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0)
            args.Add(current.ToString().Trim());

        return args;
    }

    public override string ToString() => FullName;
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

    /// <summary>
    /// Base shader names (may include template arguments like "ColorModulator<1.0f>").
    /// Use BaseShaderReferences for parsed information.
    /// </summary>
    public IReadOnlyList<string> BaseShaderNames { get; }

    /// <summary>
    /// Parsed base shader references with template arguments separated.
    /// </summary>
    public IReadOnlyList<BaseShaderReference> BaseShaderReferences { get; }

    /// <summary>
    /// Template parameters declared by this shader (e.g., "float Intensity").
    /// Empty if this shader is not a template.
    /// </summary>
    public IReadOnlyList<TemplateParameter> TemplateParameters { get; }

    /// <summary>
    /// Returns true if this shader has template parameters.
    /// </summary>
    public bool IsTemplate => TemplateParameters.Count > 0;

    public IReadOnlyList<ShaderVariable> Variables { get; }
    public IReadOnlyList<ShaderMethod> Methods { get; }
    public IReadOnlyList<ShaderComposition> Compositions { get; }

    public ParsedShader(string name, Shader shader, ClassType shaderClass, List<TemplateParameter>? templateParams = null)
    {
        // Use the actual shader class name from AST, not the filename
        // This allows detecting filename/shader name mismatches
        Name = shaderClass.Name?.Text ?? name;
        Shader = shader;
        ShaderClass = shaderClass;
        IsPartial = false;

        // Use provided template parameters or try to extract from AST (less reliable)
        TemplateParameters = templateParams?.Count > 0
            ? templateParams
            : ExtractTemplateParameters(shaderClass);

        // Extract base shader names (preserving template arguments)
        BaseShaderNames = shaderClass.BaseClasses?
            .Select(bc => GetFullBaseClassName(bc))
            .ToList() ?? new List<string>();

        // Parse base shader references
        BaseShaderReferences = BaseShaderNames
            .Select(n => new BaseShaderReference(n))
            .ToList();

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

    /// <summary>
    /// Get full base class name including template arguments.
    /// </summary>
    private static string GetFullBaseClassName(TypeBase baseClass)
    {
        // Try to get the full string representation which includes generics
        var fullName = baseClass.ToString();
        if (!string.IsNullOrEmpty(fullName) && fullName != baseClass.Name?.Text)
        {
            // Clean up any whitespace
            return Regex.Replace(fullName, @"\s+", "");
        }
        return baseClass.Name?.Text ?? "unknown";
    }

    /// <summary>
    /// Extract template parameters from shader class using reflection.
    /// </summary>
    private static List<TemplateParameter> ExtractTemplateParameters(ClassType shaderClass)
    {
        var result = new List<TemplateParameter>();

        try
        {
            // Stride's ClassType may have GenericParameters or GenericArguments property
            var genericParamsProperty = shaderClass.GetType().GetProperty("GenericParameters");
            if (genericParamsProperty != null)
            {
                var genericParams = genericParamsProperty.GetValue(shaderClass) as System.Collections.IEnumerable;
                if (genericParams != null)
                {
                    foreach (var param in genericParams)
                    {
                        var nameProperty = param.GetType().GetProperty("Name");
                        var typeProperty = param.GetType().GetProperty("Type");

                        string? name = null;
                        string? typeName = null;

                        if (nameProperty != null)
                        {
                            var nameObj = nameProperty.GetValue(param);
                            name = nameObj?.ToString();
                        }

                        if (typeProperty != null)
                        {
                            var typeObj = typeProperty.GetValue(param);
                            typeName = typeObj?.ToString();
                        }

                        if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(typeName))
                        {
                            result.Add(new TemplateParameter(name, typeName));
                        }
                    }
                }
            }
        }
        catch
        {
            // Ignore reflection errors
        }

        return result;
    }

    // Private constructor for partial shaders (from regex fallback)
    private ParsedShader(
        string name,
        IReadOnlyList<string> baseShaderNames,
        IReadOnlyList<ShaderVariable> variables,
        IReadOnlyList<ShaderMethod> methods,
        IReadOnlyList<TemplateParameter> templateParameters)
    {
        Name = name;
        Shader = null;
        ShaderClass = null;
        IsPartial = true;
        BaseShaderNames = baseShaderNames;
        BaseShaderReferences = baseShaderNames.Select(n => new BaseShaderReference(n)).ToList();
        TemplateParameters = templateParameters;
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
        List<RegexExtractedMethod> methods,
        List<TemplateParameter>? templateParams = null)
    {
        return new ParsedShader(
            name,
            baseNames,
            variables.Select(v => ShaderVariable.CreatePartial(v.Name, v.TypeName)).ToList(),
            methods.Select(m => ShaderMethod.CreatePartial(m.Name, m.ReturnType)).ToList(),
            templateParams ?? new List<TemplateParameter>()
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
        TypeName = GetFullTypeName(variable);
        IsStage = variable.Qualifiers.Contains(StrideStorageQualifier.Stage);
        IsStream = variable.Qualifiers.Contains(StrideStorageQualifier.Stream);
        IsCompose = variable.Qualifiers.Contains(StrideStorageQualifier.Compose);
        Location = variable.Span;
    }

    /// <summary>
    /// Get the full type name including array brackets if applicable.
    /// Arrays in SDSL can be declared as "Type name[]" and the parser stores
    /// array information separately from the base type name.
    /// Stride's parser may use "$array" as a special type marker for array types.
    /// </summary>
    private static string GetFullTypeName(Variable variable)
    {
        var baseType = variable.Type?.Name?.Text ?? "unknown";

        try
        {
            var typeObj = variable.Type;

            // Handle Stride's special $array type marker
            // When the base type is "$array", we need to find the actual element type
            if (baseType == "$array" && typeObj != null)
            {
                // Try to get the actual element type from the Type object
                // Stride stores array element type in various places depending on version

                // Try TypeInference property
                var typeInferenceProperty = typeObj.GetType().GetProperty("TypeInference");
                if (typeInferenceProperty != null)
                {
                    var typeInference = typeInferenceProperty.GetValue(typeObj);
                    if (typeInference != null)
                    {
                        var targetTypeProp = typeInference.GetType().GetProperty("TargetType");
                        if (targetTypeProp != null)
                        {
                            var targetType = targetTypeProp.GetValue(typeInference);
                            if (targetType != null)
                            {
                                var nameProp = targetType.GetType().GetProperty("Name");
                                if (nameProp != null)
                                {
                                    var nameObj = nameProp.GetValue(targetType);
                                    var textProp = nameObj?.GetType().GetProperty("Text");
                                    if (textProp != null)
                                    {
                                        var text = textProp.GetValue(nameObj) as string;
                                        if (!string.IsNullOrEmpty(text) && text != "$array")
                                        {
                                            return text + "[]";
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                // Try Parameters property (for generic/template types)
                var paramsProperty = typeObj.GetType().GetProperty("Parameters");
                if (paramsProperty != null)
                {
                    var parameters = paramsProperty.GetValue(typeObj) as System.Collections.IList;
                    if (parameters != null && parameters.Count > 0)
                    {
                        var firstParam = parameters[0];
                        if (firstParam != null)
                        {
                            var paramNameProp = firstParam.GetType().GetProperty("Name");
                            if (paramNameProp != null)
                            {
                                var paramNameObj = paramNameProp.GetValue(firstParam);
                                var textProp = paramNameObj?.GetType().GetProperty("Text");
                                if (textProp != null)
                                {
                                    var text = textProp.GetValue(paramNameObj) as string;
                                    if (!string.IsNullOrEmpty(text))
                                    {
                                        return text + "[]";
                                    }
                                }
                            }
                            // Try ToString on the parameter
                            var paramStr = firstParam.ToString();
                            if (!string.IsNullOrEmpty(paramStr) && paramStr != "$array")
                            {
                                // Clean up the string if needed
                                paramStr = paramStr.Trim();
                                if (!paramStr.EndsWith("[]"))
                                    return paramStr + "[]";
                                return paramStr;
                            }
                        }
                    }
                }

                // Fallback: try ToString on the type itself
                var typeString = typeObj.ToString();
                if (!string.IsNullOrEmpty(typeString) && typeString != "$array")
                {
                    // Parse out the element type from strings like "DirectLightGroup[]" or "$array<DirectLightGroup>"
                    if (typeString.Contains("<") && typeString.Contains(">"))
                    {
                        var start = typeString.IndexOf('<') + 1;
                        var end = typeString.IndexOf('>');
                        if (end > start)
                        {
                            var elementType = typeString.Substring(start, end - start).Trim();
                            return elementType + "[]";
                        }
                    }
                }

                // Last resort: return unknown[]
                return "unknown[]";
            }

            // Normal case: check if it's an array that wasn't marked with $array
            if (typeObj != null)
            {
                var typeString = typeObj.ToString();
                if (!string.IsNullOrEmpty(typeString))
                {
                    if (typeString.Contains("[]") && !baseType.Contains("[]"))
                    {
                        return baseType + "[]";
                    }
                    if (typeString.Contains(baseType + "[]"))
                    {
                        return baseType + "[]";
                    }
                }

                // Try reflection to access ArrayDimensions if available
                var arrayDimsProperty = typeObj.GetType().GetProperty("ArrayDimensions");
                if (arrayDimsProperty != null)
                {
                    var dims = arrayDimsProperty.GetValue(typeObj);
                    if (dims != null)
                    {
                        var dimsList = dims as System.Collections.IList;
                        if (dimsList != null && dimsList.Count > 0)
                        {
                            return baseType + "[]";
                        }
                    }
                }
            }

            // Check if the variable's Qualifiers ToString contains array info
            var qualString = variable.Qualifiers?.ToString() ?? "";
            if (qualString.Contains("[]"))
            {
                return baseType + "[]";
            }
        }
        catch
        {
            // Ignore reflection errors, return base type
        }

        return baseType;
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
