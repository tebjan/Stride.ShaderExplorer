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

        switch (context)
        {
            case CompletionContext.AfterColon:
                // After shader : - suggest base shaders
                items.AddRange(GetShaderCompletions(prefix));
                break;

            case CompletionContext.AfterCompose:
                // After 'compose' - suggest interface types
                items.AddRange(GetInterfaceCompletions(prefix));
                break;

            case CompletionContext.AfterBase:
                // After 'base.' - only show methods from base shaders (not local)
                if (parsed != null)
                {
                    items.AddRange(GetBaseMethodCompletions(parsed, prefix));
                }
                break;

            case CompletionContext.AfterStreams:
                // After 'streams.' - show stream variables
                if (parsed != null)
                {
                    items.AddRange(GetStreamCompletions(parsed, prefix));
                }
                break;

            case CompletionContext.Semantic:
                // After : in parameter - suggest semantics
                items.AddRange(GetSemanticCompletions(prefix));
                break;

            default:
                // General completions
                items.AddRange(GetKeywordCompletions(prefix));
                items.AddRange(GetTypeCompletions(prefix));
                items.AddRange(GetFunctionCompletions(prefix));
                items.AddRange(GetShaderCompletions(prefix));

                // Add members from current shader and base shaders
                if (parsed != null)
                {
                    items.AddRange(GetMemberCompletions(parsed, prefix));
                }
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
                // Give it higher priority (lower sort value = shown first)
                SortText = k == "streams" ? "0_streams" : "1_" + k
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
                SortText = "2_" + t
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
                SortText = "3_" + f
            });
    }

    private IEnumerable<CompletionItem> GetShaderCompletions(string prefix)
    {
        return _workspace.GetAllShaderNames()
            .Where(s => s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(s => new CompletionItem
            {
                Label = s,
                Kind = CompletionItemKind.Class,
                Detail = "Shader",
                SortText = "0_" + s
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
            .Select(s => new CompletionItem
            {
                Label = s,
                Kind = CompletionItemKind.Constant,
                Detail = "Semantic",
                SortText = "0_" + s
            });
    }

    /// <summary>
    /// Gets completions for "base." - only methods from base shaders that can be called via base.
    /// Sorted by inheritance order (closest base first).
    /// </summary>
    private IEnumerable<CompletionItem> GetBaseMethodCompletions(ParsedShader parsed, string prefix)
    {
        var items = new List<CompletionItem>();
        var seenMethods = new HashSet<string>();

        // Get methods from base shaders only (not from the current shader)
        var baseShaders = _inheritanceResolver.ResolveInheritanceChain(parsed.Name);

        for (int shaderIndex = 0; shaderIndex < baseShaders.Count; shaderIndex++)
        {
            var baseShader = baseShaders[shaderIndex];

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
                        // Sort by inheritance order (closest base first)
                        SortText = $"{shaderIndex:D2}_{method.Name}"
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

    private IEnumerable<CompletionItem> GetMemberCompletions(ParsedShader parsed, string prefix)
    {
        var items = new List<CompletionItem>();
        var seenVariables = new HashSet<string>();
        var seenMethods = new HashSet<string>();

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
                    Documentation = isInherited ? $"Inherited from {definedIn}" : $"Defined in {parsed.Name}",
                    // Sort inherited items after local ones
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
                    Documentation = isInherited ? $"Inherited from {definedIn}" : $"Defined in {parsed.Name}",
                    InsertText = method.Name + "($0)",
                    InsertTextFormat = InsertTextFormat.Snippet,
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
            return CompletionContext.AfterBase;

        // Check for streams. context - show stream variables
        if (System.Text.RegularExpressions.Regex.IsMatch(beforeCursor, @"\bstreams\.\w*$"))
            return CompletionContext.AfterStreams;

        // Check for shader inheritance context (shader Name : ...)
        if (System.Text.RegularExpressions.Regex.IsMatch(beforeCursor, @"^\s*(shader|effect|mixin)\s+\w+\s*:\s*\w*$"))
            return CompletionContext.AfterColon;

        // Check for compose context
        if (beforeCursor.Contains("compose") && !beforeCursor.Contains("="))
            return CompletionContext.AfterCompose;

        // Check for semantic context (parameter : SEMANTIC)
        if (System.Text.RegularExpressions.Regex.IsMatch(beforeCursor, @"\)\s*:\s*\w*$"))
            return CompletionContext.Semantic;

        return CompletionContext.General;
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

enum CompletionContext
{
    General,
    AfterColon,
    AfterCompose,
    AfterBase,      // After "base." - show only base shader members
    AfterStreams,   // After "streams." - show stream variables
    Semantic
}
