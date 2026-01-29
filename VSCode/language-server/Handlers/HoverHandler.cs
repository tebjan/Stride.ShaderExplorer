using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using StrideShaderLanguageServer.Services;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace StrideShaderLanguageServer.Handlers;

public class HoverHandler : HoverHandlerBase
{
    private readonly ILogger<HoverHandler> _logger;
    private readonly ShaderWorkspace _workspace;
    private readonly InheritanceResolver _inheritanceResolver;
    private readonly TextDocumentSyncHandler _syncHandler;

    public HoverHandler(
        ILogger<HoverHandler> logger,
        ShaderWorkspace workspace,
        InheritanceResolver inheritanceResolver,
        TextDocumentSyncHandler syncHandler)
    {
        _logger = logger;
        _workspace = workspace;
        _inheritanceResolver = inheritanceResolver;
        _syncHandler = syncHandler;
    }

    public override Task<Hover?> Handle(HoverParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri;
        var position = request.Position;

        _logger.LogInformation("Hover requested at {Uri}:{Line}:{Character}",
            uri, position.Line, position.Character);

        var content = _syncHandler.GetDocumentContent(uri);
        if (string.IsNullOrEmpty(content))
        {
            _logger.LogWarning("No document content for hover");
            return Task.FromResult<Hover?>(null);
        }

        var word = GetWordAtPosition(content, position);
        _logger.LogInformation("Hover word: '{Word}'", word);

        if (string.IsNullOrEmpty(word))
        {
            return Task.FromResult<Hover?>(null);
        }

        var path = uri.GetFileSystemPath();
        var currentShaderName = Path.GetFileNameWithoutExtension(path);

        // Check if it's a shader name
        var shaderInfo = _workspace.GetShaderByName(word);
        _logger.LogInformation("Shader lookup for '{Word}': {Found}", word, shaderInfo != null ? "FOUND" : "NOT FOUND");

        if (shaderInfo != null)
        {
            var parsed = _workspace.GetParsedShader(word);
            var markdown = BuildShaderHoverContent(word, parsed, shaderInfo.FilePath);

            return Task.FromResult<Hover?>(new Hover
            {
                Contents = new MarkedStringsOrMarkupContent(new MarkupContent
                {
                    Kind = MarkupKind.Markdown,
                    Value = markdown
                })
            });
        }

        // Check if it's a member of the current shader (including inherited members)
        var currentParsed = _workspace.GetParsedShader(currentShaderName);
        _logger.LogInformation("Current shader '{ShaderName}' parsed: {Parsed}", currentShaderName, currentParsed != null);

        if (currentParsed != null)
        {
            // Check variables (local and inherited)
            var variableMatch = _inheritanceResolver.FindVariable(currentParsed, word);
            _logger.LogInformation("Variable lookup for '{Word}': {Found}", word, variableMatch.Variable != null ? $"FOUND in {variableMatch.DefinedIn}" : "NOT FOUND");

            if (variableMatch.Variable != null)
            {
                var isInherited = variableMatch.DefinedIn != currentShaderName;
                var markdown = BuildVariableHoverContent(variableMatch.Variable, variableMatch.DefinedIn!, isInherited);
                return Task.FromResult<Hover?>(new Hover
                {
                    Contents = new MarkedStringsOrMarkupContent(new MarkupContent
                    {
                        Kind = MarkupKind.Markdown,
                        Value = markdown
                    })
                });
            }

            // Check methods (local and inherited)
            // Get ALL methods with this name to show full override chain
            var allMethods = _inheritanceResolver.FindAllMethodsWithName(currentParsed, word).ToList();
            if (allMethods.Count > 0)
            {
                var markdown = BuildMethodsHoverContent(allMethods, currentShaderName);
                return Task.FromResult<Hover?>(new Hover
                {
                    Contents = new MarkedStringsOrMarkupContent(new MarkupContent
                    {
                        Kind = MarkupKind.Markdown,
                        Value = markdown
                    })
                });
            }
        }

        // Check if it's an HLSL intrinsic
        var intrinsicDoc = GetIntrinsicDocumentation(word);
        if (intrinsicDoc != null)
        {
            return Task.FromResult<Hover?>(new Hover
            {
                Contents = new MarkedStringsOrMarkupContent(new MarkupContent
                {
                    Kind = MarkupKind.Markdown,
                    Value = intrinsicDoc
                })
            });
        }

        // Always show a tooltip - helps user know hover is working
        return Task.FromResult<Hover?>(new Hover
        {
            Contents = new MarkedStringsOrMarkupContent(new MarkupContent
            {
                Kind = MarkupKind.Markdown,
                Value = $"*No info available for* `{word}`"
            })
        });
    }

    private static string BuildShaderHoverContent(string name, ParsedShader? parsed, string filePath)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"## shader {name}");
        sb.AppendLine();

        if (parsed != null)
        {
            if (parsed.BaseShaderNames.Any())
            {
                sb.AppendLine($"**Inherits from:** {string.Join(", ", parsed.BaseShaderNames)}");
                sb.AppendLine();
            }

            if (parsed.Variables.Any())
            {
                sb.AppendLine($"**Variables:** {parsed.Variables.Count}");
            }

            if (parsed.Methods.Any())
            {
                sb.AppendLine($"**Methods:** {parsed.Methods.Count}");
            }

            if (parsed.Compositions.Any())
            {
                sb.AppendLine($"**Compositions:** {string.Join(", ", parsed.Compositions.Select(c => c.Name))}");
            }
        }

        sb.AppendLine();
        sb.AppendLine($"*{filePath}*");

        return sb.ToString();
    }

    private static string BuildVariableHoverContent(ShaderVariable variable, string shaderName, bool isInherited = false)
    {
        var sb = new System.Text.StringBuilder();

        var qualifiers = new List<string>();
        if (variable.IsStage) qualifiers.Add("stage");
        if (variable.IsStream) qualifiers.Add("stream");
        if (variable.IsCompose) qualifiers.Add("compose");

        var qualifierStr = qualifiers.Any() ? string.Join(" ", qualifiers) + " " : "";

        sb.AppendLine("```sdsl");
        sb.AppendLine($"{qualifierStr}{variable.TypeName} {variable.Name}");
        sb.AppendLine("```");
        sb.AppendLine();
        if (isInherited)
        {
            sb.AppendLine($"*(inherited)* from **{shaderName}**");
        }
        else
        {
            sb.AppendLine($"Defined in **{shaderName}**");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Builds hover content showing all definitions of a method in the inheritance chain.
    /// </summary>
    private static string BuildMethodsHoverContent(
        List<(ShaderMethod Method, string DefinedIn)> allMethods,
        string currentShaderName)
    {
        var sb = new System.Text.StringBuilder();

        if (allMethods.Count == 1)
        {
            // Single definition - use simple format
            var (method, definedIn) = allMethods[0];
            var isInherited = definedIn != currentShaderName;
            return BuildMethodHoverContent(method, definedIn, isInherited);
        }

        // Multiple definitions - show inheritance chain
        var firstMethod = allMethods[0].Method;
        var parameters = string.Join(", ", firstMethod.Parameters.Select(p => $"{p.TypeName} {p.Name}"));

        sb.AppendLine($"### {firstMethod.Name}({parameters})");
        sb.AppendLine();
        sb.AppendLine("**Defined in:**");

        foreach (var (method, definedIn) in allMethods)
        {
            var qualifiers = new List<string>();
            if (method.IsOverride) qualifiers.Add("override");
            if (method.IsAbstract) qualifiers.Add("abstract");
            if (method.IsStage) qualifiers.Add("stage");
            var qualifierStr = qualifiers.Any() ? $"({string.Join(", ", qualifiers)}) " : "";

            var marker = definedIn == currentShaderName ? "→ " : "  ";
            var localNote = definedIn == currentShaderName ? " *(local)*" : "";

            sb.AppendLine($"{marker}`{qualifierStr}{method.ReturnType}` from **{definedIn}**{localNote}");
        }

        return sb.ToString();
    }

    private static string BuildMethodHoverContent(ShaderMethod method, string shaderName, bool isInherited = false)
    {
        var sb = new System.Text.StringBuilder();

        var qualifiers = new List<string>();
        if (method.IsOverride) qualifiers.Add("override");
        if (method.IsAbstract) qualifiers.Add("abstract");
        if (method.IsStage) qualifiers.Add("stage");

        var qualifierStr = qualifiers.Any() ? string.Join(" ", qualifiers) + " " : "";
        var parameters = string.Join(", ", method.Parameters.Select(p => $"{p.TypeName} {p.Name}"));

        sb.AppendLine("```sdsl");
        sb.AppendLine($"{qualifierStr}{method.ReturnType} {method.Name}({parameters})");
        sb.AppendLine("```");
        sb.AppendLine();
        if (isInherited)
        {
            sb.AppendLine($"*(inherited)* from **{shaderName}**");
        }
        else
        {
            sb.AppendLine($"Defined in **{shaderName}**");
        }

        return sb.ToString();
    }

    private static string? GetIntrinsicDocumentation(string name)
    {
        // Common HLSL intrinsics with documentation
        var docs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["lerp"] = "```hlsl\nT lerp(T x, T y, S s)\n```\nReturns `x + s(y - x)`. Linear interpolation between two values.",
            ["saturate"] = "```hlsl\nT saturate(T x)\n```\nClamps the value to the range [0, 1].",
            ["dot"] = "```hlsl\nfloat dot(T x, T y)\n```\nReturns the dot product of two vectors.",
            ["cross"] = "```hlsl\nfloat3 cross(float3 x, float3 y)\n```\nReturns the cross product of two 3D vectors.",
            ["normalize"] = "```hlsl\nT normalize(T x)\n```\nReturns the normalized vector (unit length).",
            ["length"] = "```hlsl\nfloat length(T x)\n```\nReturns the length of a vector.",
            ["mul"] = "```hlsl\nT mul(T x, T y)\n```\nMultiplies matrices or vectors.",
            ["Sample"] = "```hlsl\nT texture.Sample(SamplerState s, float2 uv)\n```\nSamples a texture at the specified coordinates.",
            ["pow"] = "```hlsl\nT pow(T x, T y)\n```\nReturns x raised to the power of y.",
            ["abs"] = "```hlsl\nT abs(T x)\n```\nReturns the absolute value.",
            ["clamp"] = "```hlsl\nT clamp(T x, T min, T max)\n```\nClamps x to the range [min, max].",
            ["smoothstep"] = "```hlsl\nT smoothstep(T min, T max, T x)\n```\nReturns smooth Hermite interpolation between 0 and 1.",
            ["step"] = "```hlsl\nT step(T y, T x)\n```\nReturns 1 if x >= y, otherwise 0.",
            ["frac"] = "```hlsl\nT frac(T x)\n```\nReturns the fractional part of x."
        };

        return docs.TryGetValue(name, out var doc) ? doc : null;
    }

    private static string GetWordAtPosition(string content, Position position)
    {
        var lines = content.Split('\n');
        if (position.Line < 0 || position.Line >= lines.Length)
            return string.Empty;

        var line = lines[position.Line].TrimEnd('\r');
        if (position.Character < 0 || position.Character > line.Length)
            return string.Empty;

        // Find word boundaries
        var start = position.Character;
        var end = position.Character;

        while (start > 0 && (char.IsLetterOrDigit(line[start - 1]) || line[start - 1] == '_'))
            start--;

        while (end < line.Length && (char.IsLetterOrDigit(line[end]) || line[end] == '_'))
            end++;

        if (start >= end)
            return string.Empty;

        return line.Substring(start, end - start);
    }

    protected override HoverRegistrationOptions CreateRegistrationOptions(
        HoverCapability capability,
        ClientCapabilities clientCapabilities)
    {
        return new HoverRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForPattern("**/*.sdsl")
        };
    }
}
