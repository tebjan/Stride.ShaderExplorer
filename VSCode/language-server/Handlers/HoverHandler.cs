using System.Text.RegularExpressions;
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

    // Regex to find local variable declarations: "type varName" or "type varName = ..."
    // Note: longer type names must come first (float4x4 before float4 before float, etc.)
    private static readonly Regex LocalVarDeclRegex = new(
        @"\b(float[234]x[234]|double[234]x[234]|int[234]x[234]|" +
        @"float[234]|double[234]|half[234]|int[234]|uint[234]|bool[234]|" +
        @"Color[34]?|float|double|half|int|uint|dword|bool|" +
        @"SamplerState|SamplerComparisonState|Texture\w*)\s+(\w+)\s*(=|;|,|\))",
        RegexOptions.Compiled);

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

        var (word, memberContext) = GetWordAndContextAtPosition(content, position);
        _logger.LogInformation("Hover word: '{Word}', context: '{Context}'", word, memberContext ?? "none");

        if (string.IsNullOrEmpty(word))
        {
            return Task.FromResult<Hover?>(null);
        }

        var path = uri.GetFileSystemPath();
        var currentShaderName = Path.GetFileNameWithoutExtension(path);
        var currentParsed = _workspace.GetParsedShader(currentShaderName);

        // If we have a member context (something.word), check for swizzle or member access
        if (!string.IsNullOrEmpty(memberContext))
        {
            var memberHover = GetMemberAccessHover(memberContext, word, content, position, currentParsed, currentShaderName);
            if (memberHover != null)
                return Task.FromResult<Hover?>(memberHover);
        }

        // Check if it's a shader name
        var shaderInfo = _workspace.GetShaderByName(word);
        _logger.LogInformation("Shader lookup for '{Word}': {Found}", word, shaderInfo != null ? "FOUND" : "NOT FOUND");

        if (shaderInfo != null)
        {
            var parsed = _workspace.GetParsedShader(word);
            var markdown = BuildShaderHoverContent(word, parsed, shaderInfo.DisplayPath);

            // Check if this is a redundant base shader (add remove action only, diagnostic shows the warning)
            if (currentParsed != null && IsRedundantBaseShader(currentParsed, word, out var inheritedVia))
            {
                markdown += $"\n\nRemove: {word}";
            }

            return Task.FromResult<Hover?>(new Hover
            {
                Contents = new MarkedStringsOrMarkupContent(new MarkupContent
                {
                    Kind = MarkupKind.Markdown,
                    Value = markdown
                })
            });
        }

        // Check for local variable in scope
        var localVar = FindLocalVariable(content, position, word);
        if (localVar != null)
        {
            var markdown = BuildLocalVariableHoverContent(localVar.Value.type, word);
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
        _logger.LogInformation("Current shader '{ShaderName}' parsed: {Parsed}", currentShaderName, currentParsed != null);

        if (currentParsed != null)
        {
            // Check variables (local and inherited)
            var variableMatch = _inheritanceResolver.FindVariable(currentParsed, word);
            _logger.LogInformation("Variable lookup for '{Word}': {Found}", word, variableMatch.Variable != null ? $"FOUND in {variableMatch.DefinedIn}" : "NOT FOUND");

            if (variableMatch.Variable != null)
            {
                var isLocal = variableMatch.DefinedIn == currentShaderName;
                var markdown = BuildVariableHoverContent(variableMatch.Variable, variableMatch.DefinedIn!, !isLocal, isLocal);
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

                // Check if this is an orphaned override (local override with no base method)
                var localMethod = currentParsed.Methods.FirstOrDefault(m =>
                    string.Equals(m.Name, word, StringComparison.OrdinalIgnoreCase));
                if (localMethod?.IsOverride == true && allMethods.Count == 1)
                {
                    // Only the local override exists - suggest base shaders that define this method with same signature
                    var methodSuggestions = _inheritanceResolver.FindShadersDefiningMethodWithSignature(localMethod);
                    if (methodSuggestions.Count > 0)
                    {
                        markdown += $"\n\n---\n\n⚠️ *No base method found*\n\n*Click to add as base:* " +
                            string.Join(", ", methodSuggestions.Take(10).Select(s => $"Add: {s}"));
                        if (methodSuggestions.Count > 10)
                            markdown += $" *(+{methodSuggestions.Count - 10} more)*";
                    }
                }

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

        // Check if it's a known HLSL type
        var typeInfo = HlslTypeSystem.GetTypeInfo(word);
        if (typeInfo != null)
        {
            var markdown = BuildTypeHoverContent(typeInfo);
            return Task.FromResult<Hover?>(new Hover
            {
                Contents = new MarkedStringsOrMarkupContent(new MarkupContent
                {
                    Kind = MarkupKind.Markdown,
                    Value = markdown
                })
            });
        }

        // Check if we can suggest base shaders that define this identifier
        var suggestions = FindShaderSuggestions(word, currentParsed);
        if (suggestions.HasSuggestions)
        {
            var markdown = BuildSuggestionHover(word, suggestions);
            return Task.FromResult<Hover?>(new Hover
            {
                Contents = new MarkedStringsOrMarkupContent(new MarkupContent
                {
                    Kind = MarkupKind.Markdown,
                    Value = markdown
                })
            });
        }

        // No hover info - return null to let diagnostics show without extra noise
        return Task.FromResult<Hover?>(null);
    }

    /// <summary>
    /// Find shaders that provide access to the given identifier using smart filtering.
    /// </summary>
    private ShaderSuggestions FindShaderSuggestions(string name, ParsedShader? currentParsed)
    {
        return _inheritanceResolver.FindSmartSuggestions(name, currentParsed);
    }

    /// <summary>
    /// Build hover content with shader suggestions for undefined identifiers.
    /// Each shader name is formatted as "Add: ShaderName" so the extension makes it clickable.
    /// </summary>
    private static string BuildSuggestionHover(string name, ShaderSuggestions suggestions)
    {
        var lines = new List<string>();

        // Brief intro - click any shader to add it
        lines.Add("*Click to add as base shader:*");

        // Direct definers - where the member is actually defined
        if (suggestions.DirectDefiners.Count > 0)
        {
            lines.Add("**Defined in:** " + string.Join(", ", suggestions.DirectDefiners.Select(s => $"Add: {s}")));
        }

        // Popular shaders that provide access via inheritance
        if (suggestions.PopularInheritors.Count > 0)
        {
            lines.Add("**Also via:** " + string.Join(", ", suggestions.PopularInheritors.Select(s => $"Add: {s}")));
        }

        // Workspace/local shaders
        if (suggestions.WorkspaceInheritors.Count > 0)
        {
            lines.Add("**Local:** " + string.Join(", ", suggestions.WorkspaceInheritors.Select(s => $"Add: {s}")));
        }

        // Use double newlines for markdown paragraph breaks
        return string.Join("\n\n", lines);
    }

    /// <summary>
    /// Check if a shader is a redundant base shader in the current shader's inheritance list.
    /// Returns true if another base shader already transitively inherits from this one.
    /// </summary>
    private bool IsRedundantBaseShader(ParsedShader currentShader, string baseShaderName, out string? inheritedVia)
    {
        inheritedVia = null;
        var baseNames = currentShader.BaseShaderNames;

        // Must be in the direct base list
        if (!baseNames.Any(b => string.Equals(b, baseShaderName, StringComparison.OrdinalIgnoreCase)))
            return false;

        // Check if any other base shader transitively inherits from this one
        foreach (var otherBase in baseNames)
        {
            if (string.Equals(otherBase, baseShaderName, StringComparison.OrdinalIgnoreCase))
                continue;

            var chain = _inheritanceResolver.ResolveInheritanceChain(otherBase);
            if (chain.Any(s => string.Equals(s.Name, baseShaderName, StringComparison.OrdinalIgnoreCase)))
            {
                inheritedVia = otherBase;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Handle hover on member access (something.member) including swizzles.
    /// </summary>
    private Hover? GetMemberAccessHover(string target, string member, string content, Position position, ParsedShader? currentParsed, string currentShaderName)
    {
        _logger.LogDebug("GetMemberAccessHover: target={Target}, member={Member}", target, member);

        // Try to determine the type of the target
        string? targetType = null;

        // Check if target is a known stream type
        if (HlslTypeSystem.IsStreamType(target))
        {
            // For streams.X, look up the stream member in inherited shaders
            if (currentParsed != null)
            {
                var streamVar = _inheritanceResolver.FindVariable(currentParsed, member);
                if (streamVar.Variable != null && streamVar.Variable.IsStream)
                {
                    var isLocal = streamVar.DefinedIn == currentShaderName;
                    var markdown = BuildVariableHoverContent(streamVar.Variable, streamVar.DefinedIn!, !isLocal, isLocal);
                    return new Hover
                    {
                        Contents = new MarkedStringsOrMarkupContent(new MarkupContent
                        {
                            Kind = MarkupKind.Markdown,
                            Value = markdown
                        })
                    };
                }
            }
            // Stream member not found in parsed shaders - might still be valid
        }

        // Check if target is a local variable
        var localVar = FindLocalVariable(content, position, target);
        if (localVar != null)
        {
            targetType = localVar.Value.type;
            _logger.LogDebug("Found local var {Target} with type {Type}", target, targetType);
        }

        // Check if target is a shader member variable
        if (targetType == null && currentParsed != null)
        {
            var varMatch = _inheritanceResolver.FindVariable(currentParsed, target);
            if (varMatch.Variable != null)
            {
                targetType = varMatch.Variable.TypeName;
                _logger.LogDebug("Found shader member {Target} with type {Type}", target, targetType);
            }
        }

        // If we have a target type, check for swizzle
        if (!string.IsNullOrEmpty(targetType))
        {
            var swizzleResult = HlslTypeSystem.InferSwizzleType(targetType, member);
            if (swizzleResult.ResultType != null)
            {
                return new Hover
                {
                    Contents = new MarkedStringsOrMarkupContent(new MarkupContent
                    {
                        Kind = MarkupKind.Markdown,
                        Value = $"`{swizzleResult.ResultType}` ← swizzle of `{targetType}`"
                    })
                };
            }
            else if (swizzleResult.Error != null)
            {
                return new Hover
                {
                    Contents = new MarkedStringsOrMarkupContent(new MarkupContent
                    {
                        Kind = MarkupKind.Markdown,
                        Value = $"⚠️ {swizzleResult.Error}\n\n`{target}` is `{targetType}`"
                    })
                };
            }
        }

        // Fallback: If member looks like a swizzle pattern, show generic swizzle info
        if (IsLikelySwizzle(member))
        {
            var resultType = InferSwizzleResultType(member);
            return new Hover
            {
                Contents = new MarkedStringsOrMarkupContent(new MarkupContent
                {
                    Kind = MarkupKind.Markdown,
                    Value = $"`{resultType}` ← swizzle `.{member}`"
                })
            };
        }

        return null;
    }

    /// <summary>
    /// Check if a string looks like a swizzle (x, xy, xyz, rgba, etc.)
    /// </summary>
    private static bool IsLikelySwizzle(string s)
    {
        if (string.IsNullOrEmpty(s) || s.Length > 4)
            return false;

        var lower = s.ToLowerInvariant();
        return lower.All(c => "xyzwrgbastpq".Contains(c));
    }

    /// <summary>
    /// Infer result type from swizzle pattern (assuming float base type).
    /// </summary>
    private static string InferSwizzleResultType(string swizzle)
    {
        return swizzle.Length switch
        {
            1 => "float",
            2 => "float2",
            3 => "float3",
            4 => "float4",
            _ => "float"
        };
    }

    /// <summary>
    /// Find a local variable declaration in the content before the current position.
    /// Uses simple pattern matching to find "type varName" patterns.
    /// </summary>
    private (string type, int line)? FindLocalVariable(string content, Position position, string varName)
    {
        var lines = content.Split('\n');
        var currentLine = (int)position.Line;

        // Search backward from current line to find the variable declaration
        var braceDepth = 0;
        for (var lineIdx = currentLine; lineIdx >= 0; lineIdx--)
        {
            var lineContent = lines[lineIdx].TrimEnd('\r');

            // Count braces to track scope (going backward: } enters block, { exits)
            foreach (var c in lineContent)
            {
                if (c == '}') braceDepth++;
                else if (c == '{') braceDepth--;
            }

            // If we've exited more scopes than entered, stop
            if (braceDepth < 0)
                break;

            // Simple pattern: look for "type varName" where type is followed by the variable name
            // Pattern: word boundary, type name, whitespace, exact variable name, then = or ; or , or )
            var pattern = $@"\b(\w+)\s+{Regex.Escape(varName)}\s*[=;,\)]";
            var match = Regex.Match(lineContent, pattern);

            if (match.Success)
            {
                var typeName = match.Groups[1].Value;
                // Verify it looks like a type (not a keyword like 'return', 'if', etc.)
                if (IsLikelyTypeName(typeName))
                {
                    _logger.LogDebug("Found local var '{Var}' with type '{Type}' on line {Line}",
                        varName, typeName, lineIdx);
                    return (typeName, lineIdx);
                }
            }

            // Also check for method parameters: (type varName, ...) or (type varName)
            var paramPattern = $@"\(\s*(\w+)\s+{Regex.Escape(varName)}\s*[,\)]";
            var paramMatch = Regex.Match(lineContent, paramPattern);
            if (paramMatch.Success)
            {
                var typeName = paramMatch.Groups[1].Value;
                if (IsLikelyTypeName(typeName))
                {
                    _logger.LogDebug("Found parameter '{Var}' with type '{Type}' on line {Line}",
                        varName, typeName, lineIdx);
                    return (typeName, lineIdx);
                }
            }
        }

        _logger.LogDebug("Local var '{Var}' not found", varName);
        return null;
    }

    /// <summary>
    /// Check if a word looks like a type name (not a keyword).
    /// </summary>
    private static bool IsLikelyTypeName(string word)
    {
        // Known HLSL types
        if (HlslTypeSystem.GetTypeInfo(word) != null)
            return true;

        // Common type patterns
        if (word.StartsWith("I") && word.Length > 1 && char.IsUpper(word[1]))
            return true; // Interface types like IComputeColor

        // Words that are definitely NOT types
        var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "return", "if", "else", "for", "while", "do", "switch", "case", "break",
            "continue", "default", "const", "static", "override", "stage", "stream",
            "in", "out", "inout", "uniform", "varying", "discard", "true", "false"
        };

        if (keywords.Contains(word))
            return false;

        // Assume PascalCase words are types
        if (char.IsUpper(word[0]))
            return true;

        return false;
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

    private static string BuildVariableHoverContent(ShaderVariable variable, string shaderName, bool isInherited = false, bool isLocal = false)
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
        else if (isLocal)
        {
            sb.AppendLine("*Defined in this shader*");
        }
        else
        {
            sb.AppendLine($"*Defined in* **{shaderName}**");
        }

        return sb.ToString();
    }

    private static string BuildSwizzleHoverContent(string target, string targetType, string swizzle, string resultType)
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine("```sdsl");
        sb.AppendLine($"{resultType}  // {target}.{swizzle}");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine($"**Swizzle** of `{targetType}` → `{resultType}`");

        // Add component info
        var components = swizzle.Length;
        if (components == 1)
            sb.AppendLine($"\nExtracts component `{swizzle}` as scalar");
        else
            sb.AppendLine($"\nExtracts {components} components: `{string.Join("`, `", swizzle.ToCharArray())}`");

        return sb.ToString();
    }

    private static string BuildLocalVariableHoverContent(string typeName, string varName)
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine("```sdsl");
        sb.AppendLine($"{typeName} {varName}");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("*Local variable*");

        // Add type info if known
        var typeInfo = HlslTypeSystem.GetTypeInfo(typeName);
        if (typeInfo != null)
        {
            if (typeInfo.IsScalar)
                sb.AppendLine($"\nScalar type");
            else if (typeInfo.IsVector)
                sb.AppendLine($"\n{typeInfo.Rows}-component vector");
            else if (typeInfo.IsMatrix)
                sb.AppendLine($"\n{typeInfo.Rows}×{typeInfo.Cols} matrix");
        }

        return sb.ToString();
    }

    private static string BuildTypeHoverContent(HlslTypeSystem.TypeInfo typeInfo)
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine($"### {typeInfo.Name}");
        sb.AppendLine();

        if (typeInfo.IsScalar)
        {
            sb.AppendLine("**HLSL scalar type**");
        }
        else if (typeInfo.IsVector)
        {
            sb.AppendLine($"**HLSL vector type** ({typeInfo.Rows} components)");
            sb.AppendLine();
            sb.AppendLine($"- Base type: `{typeInfo.GetScalarTypeName()}`");
            sb.AppendLine($"- Components: `.x`, `.y`" +
                (typeInfo.Rows >= 3 ? ", `.z`" : "") +
                (typeInfo.Rows >= 4 ? ", `.w`" : ""));
        }
        else if (typeInfo.IsMatrix)
        {
            sb.AppendLine($"**HLSL matrix type** ({typeInfo.Rows}×{typeInfo.Cols})");
            sb.AppendLine();
            sb.AppendLine($"- Base type: `{typeInfo.GetScalarTypeName()}`");
            sb.AppendLine($"- Access: `[row][col]` or `._mRC`");
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
            sb.AppendLine("*Defined in this shader*");
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

    /// <summary>
    /// Get the word at position and its member access context (what's before the dot).
    /// Returns (word, contextBeforeDot) where contextBeforeDot is null if not a member access.
    /// </summary>
    private static (string word, string? memberContext) GetWordAndContextAtPosition(string content, Position position)
    {
        var lines = content.Split('\n');
        if (position.Line < 0 || position.Line >= lines.Length)
            return (string.Empty, null);

        var line = lines[position.Line].TrimEnd('\r');
        if (position.Character < 0 || position.Character > line.Length)
            return (string.Empty, null);

        // Find word boundaries
        var start = (int)position.Character;
        var end = (int)position.Character;

        while (start > 0 && (char.IsLetterOrDigit(line[start - 1]) || line[start - 1] == '_'))
            start--;

        while (end < line.Length && (char.IsLetterOrDigit(line[end]) || line[end] == '_'))
            end++;

        if (start >= end)
            return (string.Empty, null);

        var word = line.Substring(start, end - start);

        // Check if there's a dot before the word (member access)
        string? memberContext = null;
        if (start > 0 && line[start - 1] == '.')
        {
            // Find the target before the dot
            var targetEnd = start - 1;
            var targetStart = targetEnd;

            while (targetStart > 0 && (char.IsLetterOrDigit(line[targetStart - 1]) || line[targetStart - 1] == '_'))
                targetStart--;

            if (targetStart < targetEnd)
            {
                memberContext = line.Substring(targetStart, targetEnd - targetStart);
            }
        }

        return (word, memberContext);
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
