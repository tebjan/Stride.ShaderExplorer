using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace StrideShaderLanguageServer.Services;

/// <summary>
/// Provides IntelliSense completions for SDSL shaders.
/// </summary>
public class CompletionService
{
    private readonly ILogger<CompletionService> _logger;
    private readonly ShaderWorkspace _workspace;
    private readonly InheritanceResolver _inheritanceResolver;

    // SDSL Keywords
    private static readonly string[] Keywords = new[]
    {
        "shader", "effect", "mixin", "class", "struct", "namespace", "using",
        "if", "else", "for", "foreach", "while", "do", "switch", "case", "default",
        "break", "continue", "return", "discard",
        "stage", "stream", "streams", "compose", "override", "abstract", "virtual", "clone",
        "static", "const", "extern", "inline", "partial", "internal",
        "cbuffer", "rgroup", "typedef",
        "in", "out", "inout", "uniform", "shared", "groupshared",
        "nointerpolation", "linear", "centroid", "noperspective", "sample",
        "row_major", "column_major",
        "true", "false", "null", "this", "base"
    };

    // HLSL Types
    private static readonly string[] Types = new[]
    {
        "void", "bool", "int", "uint", "dword", "half", "float", "double", "fixed",
        "bool2", "bool3", "bool4", "int2", "int3", "int4", "uint2", "uint3", "uint4",
        "half2", "half3", "half4", "float2", "float3", "float4", "double2", "double3", "double4",
        "float2x2", "float3x3", "float4x4", "float2x3", "float2x4", "float3x2", "float3x4", "float4x2", "float4x3",
        "int2x2", "int3x3", "int4x4",
        "SamplerState", "SamplerComparisonState",
        "Texture1D", "Texture1DArray", "Texture2D", "Texture2DArray", "Texture2DMS", "Texture2DMSArray",
        "Texture3D", "TextureCube", "TextureCubeArray",
        "RWTexture1D", "RWTexture2D", "RWTexture2DArray", "RWTexture3D",
        "Buffer", "ByteAddressBuffer", "StructuredBuffer", "ConsumeStructuredBuffer", "AppendStructuredBuffer",
        "RWBuffer", "RWByteAddressBuffer", "RWStructuredBuffer",
        "Color", "Color3", "Color4"
    };

    // HLSL Intrinsic Functions
    private static readonly string[] Functions = new[]
    {
        // Math
        "abs", "acos", "acosh", "asin", "asinh", "atan", "atan2", "atanh",
        "ceil", "clamp", "cos", "cosh", "degrees", "exp", "exp2", "floor",
        "fma", "fmod", "frac", "frexp", "ldexp", "lerp", "log", "log10", "log2",
        "mad", "max", "min", "modf", "pow", "radians", "rcp", "round", "rsqrt",
        "saturate", "sign", "sin", "sincos", "sinh", "smoothstep", "sqrt", "step",
        "tan", "tanh", "trunc",
        // Vector
        "all", "any", "cross", "determinant", "distance", "dot", "dst",
        "faceforward", "length", "lit", "mul", "normalize", "reflect", "refract", "transpose",
        // Texture
        "Sample", "SampleBias", "SampleCmp", "SampleCmpLevelZero", "SampleGrad", "SampleLevel",
        "Load", "GetDimensions", "CalculateLevelOfDetail",
        "Gather", "GatherRed", "GatherGreen", "GatherBlue", "GatherAlpha",
        // Derivative
        "ddx", "ddx_coarse", "ddx_fine", "ddy", "ddy_coarse", "ddy_fine", "fwidth",
        // Cast
        "asfloat", "asint", "asuint", "asdouble", "f16tof32", "f32tof16",
        // Bit
        "countbits", "firstbithigh", "firstbitlow", "reversebits",
        // Barrier
        "AllMemoryBarrier", "AllMemoryBarrierWithGroupSync",
        "DeviceMemoryBarrier", "DeviceMemoryBarrierWithGroupSync",
        "GroupMemoryBarrier", "GroupMemoryBarrierWithGroupSync",
        // Interlocked
        "InterlockedAdd", "InterlockedAnd", "InterlockedCompareExchange",
        "InterlockedCompareStore", "InterlockedExchange", "InterlockedMax",
        "InterlockedMin", "InterlockedOr", "InterlockedXor",
        // Misc
        "clip", "isfinite", "isinf", "isnan", "noise"
    };

    // Common Semantics
    private static readonly string[] Semantics = new[]
    {
        "POSITION", "NORMAL", "TANGENT", "BINORMAL", "COLOR", "COLOR0", "COLOR1",
        "TEXCOORD", "TEXCOORD0", "TEXCOORD1", "TEXCOORD2", "TEXCOORD3",
        "TEXCOORD4", "TEXCOORD5", "TEXCOORD6", "TEXCOORD7",
        "SV_Position", "SV_Target", "SV_Target0", "SV_Target1", "SV_Target2", "SV_Target3",
        "SV_Depth", "SV_VertexID", "SV_InstanceID", "SV_PrimitiveID", "SV_IsFrontFace",
        "SV_DispatchThreadID", "SV_GroupID", "SV_GroupIndex", "SV_GroupThreadID"
    };

    public CompletionService(ILogger<CompletionService> logger, ShaderWorkspace workspace, InheritanceResolver inheritanceResolver)
    {
        _logger = logger;
        _workspace = workspace;
        _inheritanceResolver = inheritanceResolver;
    }

    public IEnumerable<CompletionItem> GetCompletions(string documentPath, string content, Position position)
    {
        var items = new List<CompletionItem>();
        var lineContent = GetLineContent(content, position.Line);
        var wordStart = GetWordStart(lineContent, position.Character);
        var prefix = lineContent.Substring(wordStart, position.Character - wordStart);

        _logger.LogDebug("Getting completions for prefix '{Prefix}' at {Position}", prefix, position);

        // Check context
        var context = DetermineContext(lineContent, position.Character);

        // Get parsed shader for member-related completions
        var parsed = GetParsedShaderForDocument(documentPath, content);

        switch (context.Type)
        {
            case CompletionContextType.AfterColon:
                // After shader : - suggest base shaders
                items.AddRange(GetShaderCompletions(prefix));
                break;

            case CompletionContextType.AfterCompose:
                // After 'compose' - suggest interface types
                items.AddRange(GetInterfaceCompletions(prefix));
                break;

            case CompletionContextType.AfterBase:
                // After 'base.' - show members from base shaders (not local)
                if (parsed != null)
                {
                    items.AddRange(GetBaseMemberCompletions(parsed, prefix, documentPath));
                }
                break;

            case CompletionContextType.AfterStreams:
                // After 'streams.' - show stream variables
                if (parsed != null)
                {
                    items.AddRange(GetStreamCompletions(parsed, prefix));
                }
                break;

            case CompletionContextType.AfterVariable:
                // After 'varName.' - show struct/composition/vector members
                if (parsed != null && context.VariableName != null)
                {
                    items.AddRange(GetVariableMemberCompletions(parsed, context.VariableName, prefix, content, position, documentPath));
                }
                break;

            case CompletionContextType.Semantic:
                // After : in parameter - suggest semantics
                items.AddRange(GetSemanticCompletions(prefix));
                break;

            default:
                // General completions - local members always have highest priority
                // Sort order: 0_ = local members, 1_ = inherited members, 2_ = functions, 3_ = keywords, 4_ = types, 5_ = shaders
                if (parsed != null)
                {
                    items.AddRange(GetMemberCompletions(parsed, prefix));
                }
                items.AddRange(GetFunctionCompletions(prefix));
                items.AddRange(GetKeywordCompletions(prefix));
                items.AddRange(GetTypeCompletions(prefix));
                items.AddRange(GetShaderCompletions(prefix));
                break;
        }

        return items;
    }

    private ParsedShader? GetParsedShaderForDocument(string path, string content)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        _workspace.UpdateDocument(path, content);
        return _workspace.GetParsedShader(name);
    }

    // Sort order (lower = higher priority):
    // 0_ = Local members (variables, methods)
    // 1_ = Inherited members
    // 2_ = HLSL intrinsic functions
    // 3_ = Keywords
    // 4_ = Types
    // 5_ = Shaders

    private IEnumerable<CompletionItem> GetKeywordCompletions(string prefix)
    {
        return Keywords
            .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(k => new CompletionItem
            {
                Label = k,
                Kind = CompletionItemKind.Keyword,
                Detail = "Keyword",
                // "streams" is accessed far more often than "stream" is declared
                // Give it slightly higher priority within keywords
                SortText = k == "streams" ? "3_0_streams" : "3_" + k
            });
    }

    private IEnumerable<CompletionItem> GetTypeCompletions(string prefix)
    {
        return Types
            .Where(t => t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(t => new CompletionItem
            {
                Label = t,
                Kind = CompletionItemKind.Class,
                Detail = "Type",
                SortText = "4_" + t
            });
    }

    private IEnumerable<CompletionItem> GetFunctionCompletions(string prefix)
    {
        return Functions
            .Where(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(f => new CompletionItem
            {
                Label = f,
                Kind = CompletionItemKind.Function,
                Detail = "HLSL Intrinsic",
                SortText = "2_" + f
            });
    }

    private IEnumerable<CompletionItem> GetShaderCompletions(string prefix)
    {
        return _workspace.GetAllShaderNames()
            .Where(s => s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(s =>
            {
                var parsed = _workspace.GetParsedShader(s);
                var detail = "Shader";
                var insertText = s;
                var labelDetails = (CompletionItemLabelDetails?)null;

                // Show template signature for templated shaders
                if (parsed?.IsTemplate == true)
                {
                    var templateParams = string.Join(", ", parsed.TemplateParameters.Select(p => $"{p.TypeName} {p.Name}"));
                    detail = $"Shader<{templateParams}>";
                    labelDetails = new CompletionItemLabelDetails
                    {
                        Detail = $"<{templateParams}>"
                    };
                    // Insert with template placeholder
                    var placeholders = string.Join(", ", parsed.TemplateParameters.Select((p, i) => $"${{{i + 1}:{p.Name}}}"));
                    insertText = $"{s}<{placeholders}>";
                }

                return new CompletionItem
                {
                    Label = s,
                    Kind = CompletionItemKind.Class,
                    Detail = detail,
                    LabelDetails = labelDetails,
                    InsertText = insertText,
                    InsertTextFormat = parsed?.IsTemplate == true ? InsertTextFormat.Snippet : InsertTextFormat.PlainText,
                    SortText = "5_" + s
                };
            });
    }

    private IEnumerable<CompletionItem> GetInterfaceCompletions(string prefix)
    {
        // Return shaders that start with 'I' (interfaces) or 'Compute' for compositions
        return _workspace.GetAllShaderNames()
            .Where(s => (s.StartsWith("I") || s.StartsWith("Compute")) &&
                       s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(s => new CompletionItem
            {
                Label = s,
                Kind = CompletionItemKind.Interface,
                Detail = "Composition Type",
                SortText = "0_" + s
            });
    }

    private IEnumerable<CompletionItem> GetSemanticCompletions(string prefix)
    {
        return Semantics
            .Where(s => s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(s =>
            {
                var doc = SemanticInfo.GetDocumentation(s);
                return new CompletionItem
                {
                    Label = s,
                    Kind = CompletionItemKind.Constant,
                    Detail = doc != null ? TruncateDoc(doc, 60) : "Semantic",
                    Documentation = doc,
                    LabelDetails = new CompletionItemLabelDetails
                    {
                        Description = s.StartsWith("SV_") ? "System Value" : "Input/Output"
                    },
                    SortText = s.StartsWith("SV_") ? "0_" + s : "1_" + s  // SV_ semantics first
                };
            });
    }

    /// <summary>
    /// Truncate documentation to a reasonable length for detail display.
    /// </summary>
    private static string TruncateDoc(string doc, int maxLength = 50)
    {
        if (doc.Length <= maxLength) return doc;
        var truncated = doc.Substring(0, maxLength - 3);
        var lastSpace = truncated.LastIndexOf(' ');
        if (lastSpace > maxLength / 2)
            truncated = truncated.Substring(0, lastSpace);
        return truncated + "...";
    }

    /// <summary>
    /// Gets completions for "base." - methods AND variables from base shaders.
    /// Sorted by inheritance order (closest base first).
    /// </summary>
    private IEnumerable<CompletionItem> GetBaseMemberCompletions(ParsedShader parsed, string prefix, string? contextPath)
    {
        var items = new List<CompletionItem>();
        var seenMethods = new HashSet<string>();
        var seenVariables = new HashSet<string>();

        // Get base shaders only (not from the current shader)
        var baseShaders = _inheritanceResolver.ResolveInheritanceChain(parsed.Name, contextPath);

        for (int shaderIndex = 0; shaderIndex < baseShaders.Count; shaderIndex++)
        {
            var baseShader = baseShaders[shaderIndex];

            // Methods from base shaders
            foreach (var method in baseShader.Methods)
            {
                // Skip if already seen (prefer closer base over further)
                if (!seenMethods.Add(method.Name))
                    continue;

                if (method.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var parameters = string.Join(", ", method.Parameters.Select(p => $"{p.TypeName} {p.Name}"));
                    var signature = $"{method.ReturnType} {method.Name}({parameters})";

                    items.Add(new CompletionItem
                    {
                        Label = method.Name,
                        Kind = CompletionItemKind.Method,
                        Detail = $"{signature} ({baseShader.Name})",
                        LabelDetails = new CompletionItemLabelDetails
                        {
                            Description = baseShader.Name
                        },
                        Documentation = $"Call base implementation from {baseShader.Name}",
                        InsertText = method.Name + "($0)",
                        InsertTextFormat = InsertTextFormat.Snippet,
                        // Sort by inheritance order (closest base first), methods before variables
                        SortText = $"{shaderIndex:D2}_0_{method.Name}"
                    });
                }
            }

            // Variables from base shaders
            foreach (var variable in baseShader.Variables)
            {
                // Skip if already seen (prefer closer base over further)
                if (!seenVariables.Add(variable.Name))
                    continue;

                if (variable.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var detail = variable.TypeName;
                    if (variable.IsStage) detail = "stage " + detail;
                    if (variable.IsStream) detail = "stream " + detail;

                    items.Add(new CompletionItem
                    {
                        Label = variable.Name,
                        Kind = CompletionItemKind.Field,
                        Detail = $"{detail} ({baseShader.Name})",
                        LabelDetails = new CompletionItemLabelDetails
                        {
                            Description = baseShader.Name
                        },
                        Documentation = $"Inherited variable from {baseShader.Name}",
                        // Sort by inheritance order (closest base first), variables after methods
                        SortText = $"{shaderIndex:D2}_1_{variable.Name}"
                    });
                }
            }
        }

        return items;
    }

    /// <summary>
    /// Gets completions for "streams." - stream variables from current shader and inheritance chain.
    /// Sorted by shader (local first, then by inheritance order).
    /// </summary>
    private IEnumerable<CompletionItem> GetStreamCompletions(ParsedShader parsed, string prefix)
    {
        var items = new List<CompletionItem>();
        var seenStreams = new HashSet<string>();

        // Build shader order map for sorting (local = 0, first base = 1, etc.)
        var shaderOrder = new Dictionary<string, int> { { parsed.Name, 0 } };
        var baseShaders = _inheritanceResolver.ResolveInheritanceChain(parsed.Name);
        for (int i = 0; i < baseShaders.Count; i++)
        {
            shaderOrder[baseShaders[i].Name] = i + 1;
        }

        // Get all stream variables
        foreach (var (variable, definedIn) in _inheritanceResolver.GetAllVariables(parsed))
        {
            if (!variable.IsStream)
                continue;

            if (!seenStreams.Add(variable.Name))
                continue;

            if (variable.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var isLocal = definedIn == parsed.Name;
                var order = shaderOrder.GetValueOrDefault(definedIn, 99);

                items.Add(new CompletionItem
                {
                    Label = variable.Name,
                    Kind = CompletionItemKind.Field,
                    // Always show the source shader
                    Detail = $"stream {variable.TypeName} ({definedIn})",
                    LabelDetails = new CompletionItemLabelDetails
                    {
                        Detail = ": " + variable.TypeName,  // Type shown inline after name
                        Description = definedIn
                    },
                    Documentation = isLocal
                        ? $"Stream variable defined in {parsed.Name}"
                        : $"Stream variable inherited from {definedIn}",
                    // Sort: by shader order first, then alphabetically
                    SortText = $"{order:D2}_{variable.Name}"
                });
            }
        }

        return items;
    }

    /// <summary>
    /// Gets completions for "varName." or "varName.member." - struct fields, composition members, or vector swizzles.
    /// Supports chained member access like p.Position.xy where we need to resolve each step.
    /// </summary>
    private IEnumerable<CompletionItem> GetVariableMemberCompletions(
        ParsedShader parsed,
        string memberChain,
        string prefix,
        string content,
        Position position,
        string? contextPath)
    {
        var items = new List<CompletionItem>();

        // Split chain: "p.Position" → ["p", "Position"]
        var parts = memberChain.Split('.');
        if (parts.Length == 0)
            return items;

        // Find the first variable's type
        var currentType = FindVariableType(parsed, parts[0], content, position, contextPath);
        if (currentType == null)
        {
            _logger.LogDebug("Could not determine type for variable '{VariableName}'", parts[0]);
            return items;
        }

        _logger.LogDebug("Variable '{VariableName}' has type '{Type}'", parts[0], currentType);

        // Walk the chain, resolving each member's type
        for (int i = 1; i < parts.Length; i++)
        {
            var memberName = parts[i];
            var nextType = ResolveMemberType(currentType, memberName, contextPath);
            if (nextType == null)
            {
                _logger.LogDebug("Could not resolve member '{Member}' on type '{Type}'", memberName, currentType);
                return items;
            }
            _logger.LogDebug("Member '{Member}' on '{Type}' has type '{NextType}'", memberName, currentType, nextType);
            currentType = nextType;
        }

        // Generate completions for the final type
        return GetCompletionsForType(currentType, prefix, contextPath);
    }

    /// <summary>
    /// Resolve the type of accessing a member on a given type.
    /// Handles struct fields, vector/matrix swizzles, and shader members.
    /// </summary>
    private string? ResolveMemberType(string baseType, string memberName, string? contextPath)
    {
        // Check if it's a struct type - look up field type
        var structDef = _workspace.GetStruct(baseType, contextPath);
        if (structDef != null)
        {
            var field = structDef.Fields.FirstOrDefault(f =>
                string.Equals(f.Name, memberName, StringComparison.OrdinalIgnoreCase));
            return field?.TypeName;
        }

        // Check if it's a vector/matrix type - resolve swizzle
        var typeInfo = HlslTypeSystem.GetTypeInfo(baseType);
        if (typeInfo != null && (typeInfo.IsVector || typeInfo.IsMatrix))
        {
            return ResolveSwizzleType(typeInfo, memberName);
        }

        // Check if it's a scalar type - scalars can also be swizzled (e.g., f.xx)
        if (typeInfo != null && typeInfo.IsScalar)
        {
            return ResolveScalarSwizzleType(baseType, memberName);
        }

        // Check if it's a shader/composition type - look up variable type
        var shaderForType = _workspace.GetParsedShaderClosest(baseType, contextPath);
        if (shaderForType != null)
        {
            foreach (var (variable, _) in _inheritanceResolver.GetAllVariables(shaderForType, contextPath))
            {
                if (string.Equals(variable.Name, memberName, StringComparison.OrdinalIgnoreCase))
                {
                    return variable.TypeName;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Resolve the resulting type of a vector/matrix swizzle operation.
    /// </summary>
    private string? ResolveSwizzleType(HlslTypeSystem.TypeInfo typeInfo, string swizzle)
    {
        if (string.IsNullOrEmpty(swizzle))
            return null;

        // Validate swizzle components
        var maxComponents = typeInfo.IsMatrix ? Math.Max(typeInfo.Rows, typeInfo.Cols) : typeInfo.Rows;
        if (!IsValidSwizzle(swizzle, maxComponents))
            return null;

        var scalarType = typeInfo.GetScalarTypeName();

        // Single component returns scalar
        if (swizzle.Length == 1)
            return scalarType;

        // Multiple components return vector
        return $"{scalarType}{swizzle.Length}";
    }

    /// <summary>
    /// Resolve the type of swizzling a scalar (e.g., f.xx returns float2).
    /// </summary>
    private string? ResolveScalarSwizzleType(string scalarType, string swizzle)
    {
        if (string.IsNullOrEmpty(swizzle))
            return null;

        // Scalars can only use 'x' or 'r' for swizzle
        if (!swizzle.All(c => c == 'x' || c == 'r'))
            return null;

        if (swizzle.Length > 4)
            return null;

        if (swizzle.Length == 1)
            return scalarType;

        return $"{scalarType}{swizzle.Length}";
    }

    /// <summary>
    /// Check if a swizzle string is valid for the given component count.
    /// </summary>
    private bool IsValidSwizzle(string swizzle, int maxComponents)
    {
        if (string.IsNullOrEmpty(swizzle) || swizzle.Length > 4)
            return false;

        var validXyzw = new[] { 'x', 'y', 'z', 'w' }.Take(maxComponents).ToHashSet();
        var validRgba = new[] { 'r', 'g', 'b', 'a' }.Take(maxComponents).ToHashSet();
        var validStuv = new[] { 's', 't', 'u', 'v' }.Take(maxComponents).ToHashSet();

        // All characters must be from one set (can't mix xyzw with rgba)
        return swizzle.All(c => validXyzw.Contains(c)) ||
               swizzle.All(c => validRgba.Contains(c)) ||
               swizzle.All(c => validStuv.Contains(c));
    }

    /// <summary>
    /// Generate completions for a given type (struct fields, swizzles, or shader members).
    /// </summary>
    private IEnumerable<CompletionItem> GetCompletionsForType(string typeName, string prefix, string? contextPath)
    {
        // Check if it's a struct type - show struct fields
        if (_workspace.IsStructType(typeName, contextPath))
        {
            return GetStructFieldCompletions(typeName, prefix, contextPath);
        }

        // Check if it's a shader/composition type - show shader members
        var shaderForType = _workspace.GetParsedShaderClosest(typeName, contextPath);
        if (shaderForType != null)
        {
            return GetCompositionMemberCompletions(shaderForType, prefix, contextPath);
        }

        // Check if it's a vector/matrix type - show swizzle components
        var typeInfo = HlslTypeSystem.GetTypeInfo(typeName);
        if (typeInfo != null && (typeInfo.IsVector || typeInfo.IsMatrix))
        {
            return GetSwizzleCompletions(typeInfo, prefix);
        }

        // Check if it's a scalar type - scalars can be swizzled too
        if (typeInfo != null && typeInfo.IsScalar)
        {
            return GetScalarSwizzleCompletions(typeInfo, prefix);
        }

        return Enumerable.Empty<CompletionItem>();
    }

    /// <summary>
    /// Gets swizzle completions for scalar types (can repeat: f.xx, f.xxx, f.xxxx).
    /// </summary>
    private IEnumerable<CompletionItem> GetScalarSwizzleCompletions(HlslTypeSystem.TypeInfo typeInfo, string prefix)
    {
        var items = new List<CompletionItem>();
        var scalarType = typeInfo.GetScalarTypeName();

        // Single component x or r
        if ("x".StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            items.Add(new CompletionItem
            {
                Label = "x",
                Kind = CompletionItemKind.Field,
                Detail = scalarType,
                Documentation = "Scalar as single component",
                SortText = "0_00_x"
            });
        }
        if ("r".StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            items.Add(new CompletionItem
            {
                Label = "r",
                Kind = CompletionItemKind.Field,
                Detail = scalarType,
                Documentation = "Scalar as single component",
                SortText = "0_01_r"
            });
        }

        // Common repeated swizzles (xx, xxx, xxxx)
        AddSwizzle(items, "xx", $"{scalarType}2", prefix, 10);
        AddSwizzle(items, "xxx", $"{scalarType}3", prefix, 11);
        AddSwizzle(items, "xxxx", $"{scalarType}4", prefix, 12);

        return items;
    }

    private void AddSwizzle(List<CompletionItem> items, string swizzle, string resultType, string prefix, int sortIndex)
    {
        if (swizzle.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            items.Add(new CompletionItem
            {
                Label = swizzle,
                Kind = CompletionItemKind.Field,
                Detail = resultType,
                Documentation = "Swizzle operator",
                SortText = $"1_{sortIndex:D2}_{swizzle}"
            });
        }
    }

    /// <summary>
    /// Finds the type of a variable in the current context.
    /// Searches: local method variables, shader variables, compositions, inherited members.
    /// </summary>
    private string? FindVariableType(
        ParsedShader parsed,
        string variableName,
        string content,
        Position position,
        string? contextPath)
    {
        // First check if it's a shader/class variable or composition
        foreach (var (variable, _) in _inheritanceResolver.GetAllVariables(parsed, contextPath))
        {
            if (variable.Name == variableName)
            {
                return variable.TypeName;
            }
        }

        // Check compositions specifically
        foreach (var (comp, _) in _inheritanceResolver.GetAllCompositions(parsed, contextPath))
        {
            if (comp.Name == variableName)
            {
                return comp.TypeName;
            }
        }

        // Try to find local variable declarations in the current method body
        // Parse the content to find declarations like "Type varName" or "Type varName = ..."
        var localType = FindLocalVariableType(content, variableName, position);
        if (localType != null)
        {
            return localType;
        }

        return null;
    }

    /// <summary>
    /// Finds the type of a local variable by scanning method body for declarations.
    /// </summary>
    private string? FindLocalVariableType(string content, string variableName, Position position)
    {
        var lines = content.Split('\n');

        // Look for variable declarations before the current position
        // Pattern: Type varName (with optional = or ;)
        var declarationPattern = new System.Text.RegularExpressions.Regex(
            $@"^\s*(\w+(?:<[^>]+>)?)\s+{System.Text.RegularExpressions.Regex.Escape(variableName)}\s*[=;,\)]",
            System.Text.RegularExpressions.RegexOptions.Multiline);

        // Search from start of file up to current position
        for (int i = 0; i <= position.Line && i < lines.Length; i++)
        {
            var line = lines[i];
            var match = declarationPattern.Match(line);
            if (match.Success)
            {
                var typeName = match.Groups[1].Value;
                // Skip keywords that look like types but aren't
                if (typeName != "return" && typeName != "if" && typeName != "for" &&
                    typeName != "while" && typeName != "switch" && typeName != "case")
                {
                    return typeName;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Gets struct field completions for a given struct type.
    /// </summary>
    private IEnumerable<CompletionItem> GetStructFieldCompletions(string structTypeName, string prefix, string? contextPath)
    {
        var fields = _workspace.GetStructFields(structTypeName, contextPath);

        return fields
            .Where(f => f.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select((f, index) =>
            {
                var typeDisplay = f.TypeName + (f.IsArray ? "[]" : "");
                return new CompletionItem
                {
                    Label = f.Name,
                    Kind = CompletionItemKind.Field,
                    Detail = typeDisplay,
                    LabelDetails = new CompletionItemLabelDetails
                    {
                        Detail = ": " + typeDisplay  // Shows inline after field name
                    },
                    Documentation = $"Field of struct {structTypeName}",
                    SortText = $"0_{index:D2}_{f.Name}"  // Keep original order from struct definition
                };
            });
    }

    /// <summary>
    /// Gets member completions for a composition variable (shader type).
    /// </summary>
    private IEnumerable<CompletionItem> GetCompositionMemberCompletions(ParsedShader shaderType, string prefix, string? contextPath)
    {
        var items = new List<CompletionItem>();
        var seenMethods = new HashSet<string>();
        var seenVariables = new HashSet<string>();

        // Get all methods from the composition's shader and its bases
        foreach (var (method, definedIn) in _inheritanceResolver.GetAllMethods(shaderType, contextPath))
        {
            if (!seenMethods.Add(method.Name))
                continue;

            if (method.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var parameters = string.Join(", ", method.Parameters.Select(p => $"{p.TypeName} {p.Name}"));
                var signature = $"{method.ReturnType} {method.Name}({parameters})";

                items.Add(new CompletionItem
                {
                    Label = method.Name,
                    Kind = CompletionItemKind.Method,
                    Detail = signature,
                    LabelDetails = new CompletionItemLabelDetails
                    {
                        Description = definedIn
                    },
                    Documentation = $"Method from {definedIn}",
                    InsertText = method.Name + "($0)",
                    InsertTextFormat = InsertTextFormat.Snippet,
                    SortText = $"0_{method.Name}"  // Methods first
                });
            }
        }

        // Get all variables from the composition's shader and its bases
        foreach (var (variable, definedIn) in _inheritanceResolver.GetAllVariables(shaderType, contextPath))
        {
            if (!seenVariables.Add(variable.Name))
                continue;

            // Skip streams - they're accessed via streams., not composition.
            if (variable.IsStream)
                continue;

            if (variable.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var detail = variable.TypeName;
                if (variable.IsStage) detail = "stage " + detail;

                items.Add(new CompletionItem
                {
                    Label = variable.Name,
                    Kind = CompletionItemKind.Field,
                    Detail = detail,
                    LabelDetails = new CompletionItemLabelDetails
                    {
                        Detail = ": " + variable.TypeName,  // Type shown inline after name
                        Description = definedIn
                    },
                    Documentation = $"Variable from {definedIn}",
                    SortText = $"1_{variable.Name}"  // Variables after methods
                });
            }
        }

        return items;
    }

    /// <summary>
    /// Gets vector/matrix swizzle completions (x, y, z, w, r, g, b, a, xy, xyz, etc.)
    /// </summary>
    private IEnumerable<CompletionItem> GetSwizzleCompletions(HlslTypeSystem.TypeInfo typeInfo, string prefix)
    {
        var items = new List<CompletionItem>();
        var componentCount = typeInfo.IsMatrix ? Math.Max(typeInfo.Rows, typeInfo.Cols) : typeInfo.Rows;

        // XYZW components
        var xyzw = new[] { "x", "y", "z", "w" }.Take(componentCount).ToArray();
        // RGBA components
        var rgba = new[] { "r", "g", "b", "a" }.Take(componentCount).ToArray();

        var scalarType = typeInfo.GetScalarTypeName();
        int sortIndex = 0;

        // Single components - highest priority
        foreach (var c in xyzw)
        {
            if (c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                items.Add(new CompletionItem
                {
                    Label = c,
                    Kind = CompletionItemKind.Field,
                    Detail = scalarType,
                    Documentation = "Vector component",
                    SortText = $"0_{sortIndex++:D2}_{c}"
                });
            }
        }

        foreach (var c in rgba)
        {
            if (c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                items.Add(new CompletionItem
                {
                    Label = c,
                    Kind = CompletionItemKind.Field,
                    Detail = scalarType,
                    Documentation = "Color component",
                    SortText = $"0_{sortIndex++:D2}_{c}"
                });
            }
        }

        // Common 2-component swizzles
        if (componentCount >= 2)
        {
            AddSwizzle(items, "xy", $"{scalarType}2", prefix, ref sortIndex);
            AddSwizzle(items, "rg", $"{scalarType}2", prefix, ref sortIndex);
        }

        // Common 3-component swizzles
        if (componentCount >= 3)
        {
            AddSwizzle(items, "xyz", $"{scalarType}3", prefix, ref sortIndex);
            AddSwizzle(items, "rgb", $"{scalarType}3", prefix, ref sortIndex);
        }

        // Common 4-component swizzles
        if (componentCount >= 4)
        {
            AddSwizzle(items, "xyzw", $"{scalarType}4", prefix, ref sortIndex);
            AddSwizzle(items, "rgba", $"{scalarType}4", prefix, ref sortIndex);
        }

        return items;
    }

    private void AddSwizzle(List<CompletionItem> items, string swizzle, string resultType, string prefix, ref int sortIndex)
    {
        if (swizzle.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            items.Add(new CompletionItem
            {
                Label = swizzle,
                Kind = CompletionItemKind.Field,
                Detail = resultType,
                Documentation = "Swizzle operator",
                SortText = $"1_{sortIndex++:D2}_{swizzle}"  // Multi-component swizzles after single components
            });
        }
    }

    /// <summary>
    /// Gets member completions (variables, methods, and structs) from current shader and inheritance chain.
    /// Local members always get highest priority (0_), inherited members get second priority (1_).
    /// </summary>
    private IEnumerable<CompletionItem> GetMemberCompletions(ParsedShader parsed, string prefix)
    {
        var items = new List<CompletionItem>();
        var seenVariables = new HashSet<string>();
        var seenMethods = new HashSet<string>();
        var seenStructs = new HashSet<string>();

        // Structs (local and inherited) - show as types
        foreach (var (structDef, definedIn) in _inheritanceResolver.GetAllStructs(parsed))
        {
            // Skip duplicates (prefer local over inherited)
            if (!seenStructs.Add(structDef.Name))
                continue;

            if (structDef.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var isInherited = definedIn != parsed.Name;
                var fieldCount = structDef.Fields.Count;
                var detail = $"struct ({fieldCount} fields)";
                if (isInherited) detail += $" (from {definedIn})";

                items.Add(new CompletionItem
                {
                    Label = structDef.Name,
                    Kind = CompletionItemKind.Struct,
                    Detail = detail,
                    LabelDetails = isInherited ? new CompletionItemLabelDetails { Description = definedIn } : null,
                    Documentation = isInherited ? $"Struct inherited from {definedIn}" : $"Struct defined in {parsed.Name}",
                    // Local = 0_, Inherited = 1_
                    SortText = (isInherited ? "1_" : "0_") + structDef.Name
                });
            }
        }

        // Variables (local and inherited)
        foreach (var (variable, definedIn) in _inheritanceResolver.GetAllVariables(parsed))
        {
            // Skip duplicates (prefer local over inherited)
            if (!seenVariables.Add(variable.Name))
                continue;

            if (variable.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var isInherited = definedIn != parsed.Name;
                var detail = variable.TypeName;
                if (variable.IsStage) detail = "stage " + detail;
                if (variable.IsStream) detail = "stream " + detail;
                if (variable.IsCompose) detail = "compose " + detail;
                if (isInherited) detail += $" (from {definedIn})";

                items.Add(new CompletionItem
                {
                    Label = variable.Name,
                    Kind = CompletionItemKind.Field,
                    Detail = detail,
                    LabelDetails = new CompletionItemLabelDetails
                    {
                        Detail = ": " + variable.TypeName,  // Type shown inline after name
                        Description = isInherited ? definedIn : null
                    },
                    Documentation = isInherited ? $"Inherited from {definedIn}" : $"Defined in {parsed.Name}",
                    // Local = 0_, Inherited = 1_
                    SortText = (isInherited ? "1_" : "0_") + variable.Name
                });
            }
        }

        // Methods (local and inherited)
        foreach (var (method, definedIn) in _inheritanceResolver.GetAllMethods(parsed))
        {
            // Skip duplicates (prefer local over inherited - handles overrides)
            if (!seenMethods.Add(method.Name))
                continue;

            if (method.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var isInherited = definedIn != parsed.Name;
                var parameters = string.Join(", ", method.Parameters.Select(p => $"{p.TypeName} {p.Name}"));
                var signature = $"{method.ReturnType} {method.Name}({parameters})";
                if (isInherited) signature += $" (from {definedIn})";

                items.Add(new CompletionItem
                {
                    Label = method.Name,
                    Kind = CompletionItemKind.Method,
                    Detail = signature,
                    LabelDetails = isInherited ? new CompletionItemLabelDetails { Description = definedIn } : null,
                    Documentation = isInherited ? $"Inherited from {definedIn}" : $"Defined in {parsed.Name}",
                    InsertText = method.Name + "($0)",
                    InsertTextFormat = InsertTextFormat.Snippet,
                    // Local = 0_, Inherited = 1_
                    SortText = (isInherited ? "1_" : "0_") + method.Name
                });
            }
        }

        return items;
    }

    private static CompletionContext DetermineContext(string line, int character)
    {
        var beforeCursor = line.Substring(0, Math.Min(character, line.Length));

        // Check for base. context - show only inherited members
        if (System.Text.RegularExpressions.Regex.IsMatch(beforeCursor, @"\bbase\.\w*$"))
            return new CompletionContext(CompletionContextType.AfterBase);

        // Check for streams. context - show stream variables
        if (System.Text.RegularExpressions.Regex.IsMatch(beforeCursor, @"\bstreams\.\w*$"))
            return new CompletionContext(CompletionContextType.AfterStreams);

        // Check for shader inheritance context (shader Name : ...)
        if (System.Text.RegularExpressions.Regex.IsMatch(beforeCursor, @"^\s*(shader|effect|mixin)\s+\w+\s*:\s*\w*$"))
            return new CompletionContext(CompletionContextType.AfterColon);

        // Check for compose context
        if (beforeCursor.Contains("compose") && !beforeCursor.Contains("="))
            return new CompletionContext(CompletionContextType.AfterCompose);

        // Check for semantic context - multiple patterns:
        // 1. After function param closing paren: "float4 pos) : SV_"
        // 2. After variable/field declaration: "float4 position : POS"
        // 3. After struct field: "float4 pos : "
        var semanticPatterns = new[]
        {
            @"\)\s*:\s*\w*$",              // After closing paren (function return/param)
            @"\w+\s+\w+\s*:\s*\w*$",       // After "type name :" pattern (field/variable)
        };
        if (semanticPatterns.Any(p => System.Text.RegularExpressions.Regex.IsMatch(beforeCursor, p)))
            return new CompletionContext(CompletionContextType.Semantic);

        // Check for variable member access (varName. or varName.member.) - captures the full chain
        // Pattern: captures "p" from "p." OR "p.Position" from "p.Position."
        var varDotMatch = System.Text.RegularExpressions.Regex.Match(beforeCursor, @"\b(\w+(?:\.\w+)*)\.\w*$");
        if (varDotMatch.Success)
        {
            var memberChain = varDotMatch.Groups[1].Value;
            var firstPart = memberChain.Split('.')[0];
            // Exclude keywords that have their own handling (base, streams) and common non-variable contexts
            if (firstPart != "base" && firstPart != "streams" && firstPart != "this")
            {
                return new CompletionContext(CompletionContextType.AfterVariable, memberChain);
            }
        }

        return new CompletionContext(CompletionContextType.General);
    }

    private static string GetLineContent(string content, int line)
    {
        var lines = content.Split('\n');
        if (line < 0 || line >= lines.Length)
            return string.Empty;
        return lines[line].TrimEnd('\r');
    }

    private static int GetWordStart(string line, int character)
    {
        var start = Math.Min(character, line.Length);
        while (start > 0 && (char.IsLetterOrDigit(line[start - 1]) || line[start - 1] == '_'))
            start--;
        return start;
    }
}

enum CompletionContextType
{
    General,
    AfterColon,
    AfterCompose,
    AfterBase,        // After "base." - show only base shader members
    AfterStreams,     // After "streams." - show stream variables
    AfterVariable,    // After "varName." - show struct/composition/vector members
    Semantic
}

/// <summary>
/// Represents completion context with optional additional data like the variable name.
/// </summary>
class CompletionContext
{
    public CompletionContextType Type { get; }
    public string? VariableName { get; }

    public CompletionContext(CompletionContextType type, string? variableName = null)
    {
        Type = type;
        VariableName = variableName;
    }

    // Implicit conversion from enum for backwards compatibility
    public static implicit operator CompletionContext(CompletionContextType type) => new CompletionContext(type);
}
