using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using StrideShaderLanguageServer.Services;

namespace StrideShaderLanguageServer.Handlers;

/// <summary>
/// Provides signature help when typing method calls.
/// Shows parameter info, overloads, and documentation.
/// </summary>
public class SignatureHelpHandler : SignatureHelpHandlerBase
{
    private readonly ILogger<SignatureHelpHandler> _logger;
    private readonly ShaderWorkspace _workspace;
    private readonly InheritanceResolver _inheritanceResolver;
    private readonly TextDocumentSyncHandler _syncHandler;

    public SignatureHelpHandler(
        ILogger<SignatureHelpHandler> logger,
        ShaderWorkspace workspace,
        InheritanceResolver inheritanceResolver,
        TextDocumentSyncHandler syncHandler)
    {
        _logger = logger;
        _workspace = workspace;
        _inheritanceResolver = inheritanceResolver;
        _syncHandler = syncHandler;
    }

    public override Task<SignatureHelp?> Handle(SignatureHelpParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri;
        var position = request.Position;

        _logger.LogDebug("SignatureHelp at {Uri}:{Line}:{Char}", uri, position.Line, position.Character);

        var content = _syncHandler.GetDocumentContent(uri);
        if (string.IsNullOrEmpty(content))
            return Task.FromResult<SignatureHelp?>(null);

        // Find the method being called
        var (methodName, argIndex) = FindMethodCallContext(content, position);
        if (string.IsNullOrEmpty(methodName))
            return Task.FromResult<SignatureHelp?>(null);

        _logger.LogDebug("Method call: {Method}, arg index: {Index}", methodName, argIndex);

        var path = uri.GetFileSystemPath();
        var currentShaderName = Path.GetFileNameWithoutExtension(path);
        var currentParsed = _workspace.GetParsedShaderClosest(currentShaderName, path);

        // Collect signatures from various sources
        var signatures = new List<SignatureInformation>();

        // 1. Check HLSL intrinsics
        var hlslSignatures = GetHlslIntrinsicSignatures(methodName);
        signatures.AddRange(hlslSignatures);

        // 2. Check methods from current shader and inheritance chain
        if (currentParsed != null)
        {
            var shaderSignatures = GetShaderMethodSignatures(currentParsed, methodName, path);
            signatures.AddRange(shaderSignatures);
        }

        // 3. Check type constructors (float4, float3x3, etc.)
        var constructorSigs = GetTypeConstructorSignatures(methodName);
        signatures.AddRange(constructorSigs);

        if (signatures.Count == 0)
            return Task.FromResult<SignatureHelp?>(null);

        return Task.FromResult<SignatureHelp?>(new SignatureHelp
        {
            Signatures = new Container<SignatureInformation>(signatures),
            ActiveSignature = 0,
            ActiveParameter = Math.Max(0, argIndex)
        });
    }

    /// <summary>
    /// Find the method name being called and which argument we're on.
    /// </summary>
    private static (string? methodName, int argIndex) FindMethodCallContext(string content, Position position)
    {
        var lines = content.Split('\n');
        if (position.Line < 0 || position.Line >= lines.Length)
            return (null, 0);

        var line = lines[position.Line].TrimEnd('\r');
        var col = Math.Min((int)position.Character, line.Length);

        // Search backward for opening paren
        var parenDepth = 0;
        var argIndex = 0;

        for (var i = col - 1; i >= 0; i--)
        {
            var c = line[i];
            if (c == ')')
            {
                parenDepth++;
            }
            else if (c == '(')
            {
                if (parenDepth > 0)
                    parenDepth--;
                else
                {
                    // Found the opening paren - extract method name before it
                    var nameEnd = i;
                    var nameStart = i - 1;
                    while (nameStart >= 0 && (char.IsLetterOrDigit(line[nameStart]) || line[nameStart] == '_'))
                        nameStart--;
                    nameStart++;

                    if (nameStart < nameEnd)
                    {
                        var methodName = line.Substring(nameStart, nameEnd - nameStart);
                        return (methodName, argIndex);
                    }
                    break;
                }
            }
            else if (c == ',' && parenDepth == 0)
            {
                argIndex++;
            }
        }

        return (null, 0);
    }

    /// <summary>
    /// Get signatures for HLSL intrinsic functions.
    /// </summary>
    private static List<SignatureInformation> GetHlslIntrinsicSignatures(string methodName)
    {
        var signatures = new List<SignatureInformation>();

        // Common HLSL intrinsics with their signatures
        var intrinsics = new Dictionary<string, (string signature, string doc, string[] paramDocs)[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["lerp"] = new[]
            {
                ("T lerp(T x, T y, S s)", "Linear interpolation: x + s*(y-x)", new[] { "Start value", "End value", "Interpolation factor (0 to 1)" })
            },
            ["saturate"] = new[]
            {
                ("T saturate(T x)", "Clamps value to [0, 1] range", new[] { "Value to clamp" })
            },
            ["clamp"] = new[]
            {
                ("T clamp(T x, T min, T max)", "Clamps value to [min, max] range", new[] { "Value to clamp", "Minimum value", "Maximum value" })
            },
            ["dot"] = new[]
            {
                ("float dot(T x, T y)", "Returns dot product of two vectors", new[] { "First vector", "Second vector" })
            },
            ["cross"] = new[]
            {
                ("float3 cross(float3 x, float3 y)", "Returns cross product of two 3D vectors", new[] { "First vector", "Second vector" })
            },
            ["normalize"] = new[]
            {
                ("T normalize(T x)", "Returns normalized vector (unit length)", new[] { "Vector to normalize" })
            },
            ["length"] = new[]
            {
                ("float length(T x)", "Returns length/magnitude of vector", new[] { "Vector" })
            },
            ["distance"] = new[]
            {
                ("float distance(T x, T y)", "Returns distance between two points", new[] { "First point", "Second point" })
            },
            ["mul"] = new[]
            {
                ("T mul(T x, T y)", "Matrix/vector multiplication", new[] { "Left operand (matrix or vector)", "Right operand (matrix or vector)" })
            },
            ["pow"] = new[]
            {
                ("T pow(T x, T y)", "Returns x raised to power y", new[] { "Base value", "Exponent" })
            },
            ["abs"] = new[]
            {
                ("T abs(T x)", "Returns absolute value", new[] { "Input value" })
            },
            ["min"] = new[]
            {
                ("T min(T x, T y)", "Returns minimum of two values", new[] { "First value", "Second value" })
            },
            ["max"] = new[]
            {
                ("T max(T x, T y)", "Returns maximum of two values", new[] { "First value", "Second value" })
            },
            ["floor"] = new[]
            {
                ("T floor(T x)", "Returns largest integer <= x", new[] { "Input value" })
            },
            ["ceil"] = new[]
            {
                ("T ceil(T x)", "Returns smallest integer >= x", new[] { "Input value" })
            },
            ["frac"] = new[]
            {
                ("T frac(T x)", "Returns fractional part of x", new[] { "Input value" })
            },
            ["step"] = new[]
            {
                ("T step(T y, T x)", "Returns 0 if x < y, else 1", new[] { "Edge/threshold value", "Value to test" })
            },
            ["smoothstep"] = new[]
            {
                ("T smoothstep(T min, T max, T x)", "Smooth Hermite interpolation", new[] { "Lower edge", "Upper edge", "Value" })
            },
            ["reflect"] = new[]
            {
                ("T reflect(T i, T n)", "Reflects incident vector about normal", new[] { "Incident vector", "Normal vector (should be normalized)" })
            },
            ["refract"] = new[]
            {
                ("T refract(T i, T n, float eta)", "Computes refraction vector", new[] { "Incident vector", "Normal vector", "Index of refraction ratio" })
            },
            ["sin"] = new[] { ("T sin(T x)", "Returns sine of x (radians)", new[] { "Angle in radians" }) },
            ["cos"] = new[] { ("T cos(T x)", "Returns cosine of x (radians)", new[] { "Angle in radians" }) },
            ["tan"] = new[] { ("T tan(T x)", "Returns tangent of x (radians)", new[] { "Angle in radians" }) },
            ["asin"] = new[] { ("T asin(T x)", "Returns arcsine of x", new[] { "Value in [-1, 1]" }) },
            ["acos"] = new[] { ("T acos(T x)", "Returns arccosine of x", new[] { "Value in [-1, 1]" }) },
            ["atan"] = new[] { ("T atan(T x)", "Returns arctangent of x", new[] { "Input value" }) },
            ["atan2"] = new[] { ("T atan2(T y, T x)", "Returns arctangent of y/x", new[] { "Y coordinate", "X coordinate" }) },
            ["sqrt"] = new[] { ("T sqrt(T x)", "Returns square root", new[] { "Input value (must be >= 0)" }) },
            ["rsqrt"] = new[] { ("T rsqrt(T x)", "Returns reciprocal square root (1/sqrt(x))", new[] { "Input value (must be > 0)" }) },
            ["exp"] = new[] { ("T exp(T x)", "Returns e^x", new[] { "Exponent" }) },
            ["exp2"] = new[] { ("T exp2(T x)", "Returns 2^x", new[] { "Exponent" }) },
            ["log"] = new[] { ("T log(T x)", "Returns natural logarithm", new[] { "Input value (must be > 0)" }) },
            ["log2"] = new[] { ("T log2(T x)", "Returns base-2 logarithm", new[] { "Input value (must be > 0)" }) },
            ["log10"] = new[] { ("T log10(T x)", "Returns base-10 logarithm", new[] { "Input value (must be > 0)" }) },
            ["sign"] = new[] { ("T sign(T x)", "Returns -1, 0, or 1 based on sign of x", new[] { "Input value" }) },
            ["round"] = new[] { ("T round(T x)", "Rounds to nearest integer", new[] { "Input value" }) },
            ["trunc"] = new[] { ("T trunc(T x)", "Truncates to integer part", new[] { "Input value" }) },
            ["fmod"] = new[] { ("T fmod(T x, T y)", "Returns floating-point remainder", new[] { "Dividend", "Divisor" }) },
            ["radians"] = new[] { ("T radians(T degrees)", "Converts degrees to radians", new[] { "Angle in degrees" }) },
            ["degrees"] = new[] { ("T degrees(T radians)", "Converts radians to degrees", new[] { "Angle in radians" }) },
            ["ddx"] = new[] { ("T ddx(T x)", "Partial derivative in screen-space x", new[] { "Value to differentiate" }) },
            ["ddy"] = new[] { ("T ddy(T x)", "Partial derivative in screen-space y", new[] { "Value to differentiate" }) },
            ["fwidth"] = new[] { ("T fwidth(T x)", "Returns abs(ddx(x)) + abs(ddy(x))", new[] { "Value" }) },
            ["any"] = new[] { ("bool any(T x)", "Returns true if any component is non-zero", new[] { "Vector or scalar" }) },
            ["all"] = new[] { ("bool all(T x)", "Returns true if all components are non-zero", new[] { "Vector or scalar" }) },
            ["transpose"] = new[] { ("matrix transpose(matrix m)", "Returns transposed matrix", new[] { "Input matrix" }) },
            ["determinant"] = new[] { ("float determinant(matrix m)", "Returns matrix determinant", new[] { "Input matrix" }) },
            ["Sample"] = new[]
            {
                ("float4 Sample(SamplerState s, float2 uv)", "Samples texture at UV coordinates", new[] { "Sampler state", "UV coordinates" }),
                ("float4 Sample(SamplerState s, float2 uv, int2 offset)", "Samples texture with texel offset", new[] { "Sampler state", "UV coordinates", "Texel offset" })
            },
            ["SampleLevel"] = new[]
            {
                ("float4 SampleLevel(SamplerState s, float2 uv, float lod)", "Samples texture at specific mip level", new[] { "Sampler state", "UV coordinates", "Mip level" })
            },
            ["SampleGrad"] = new[]
            {
                ("float4 SampleGrad(SamplerState s, float2 uv, float2 ddx, float2 ddy)", "Samples texture with gradient", new[] { "Sampler state", "UV coordinates", "X gradient", "Y gradient" })
            },
            ["Load"] = new[]
            {
                ("float4 Load(int3 location)", "Loads texel at integer coordinates", new[] { "Integer coordinates (x, y, mip)" })
            }
        };

        if (intrinsics.TryGetValue(methodName, out var overloads))
        {
            foreach (var (sig, doc, paramDocs) in overloads)
            {
                var parameters = ParseParameters(sig).Select((p, i) => new ParameterInformation
                {
                    Label = p,
                    Documentation = i < paramDocs.Length ? paramDocs[i] : null
                }).ToList();

                signatures.Add(new SignatureInformation
                {
                    Label = sig,
                    Documentation = doc,
                    Parameters = new Container<ParameterInformation>(parameters)
                });
            }
        }

        return signatures;
    }

    /// <summary>
    /// Get signatures for methods defined in the shader hierarchy.
    /// </summary>
    private List<SignatureInformation> GetShaderMethodSignatures(ParsedShader shader, string methodName, string? contextFilePath)
    {
        var signatures = new List<SignatureInformation>();
        var seenSignatures = new HashSet<string>();

        foreach (var (method, definedIn) in _inheritanceResolver.GetAllMethods(shader, contextFilePath))
        {
            if (!method.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase))
                continue;

            var paramStr = string.Join(", ", method.Parameters.Select(p => $"{p.TypeName} {p.Name}"));
            var sigLabel = $"{method.ReturnType} {method.Name}({paramStr})";

            // Avoid duplicate signatures
            if (!seenSignatures.Add(sigLabel))
                continue;

            var parameters = method.Parameters.Select(p => new ParameterInformation
            {
                Label = $"{p.TypeName} {p.Name}",
                Documentation = (string?)null
            }).ToList();

            var doc = $"Defined in **{definedIn}**";
            if (method.IsOverride)
                doc = $"override - {doc}";
            if (method.IsAbstract)
                doc = $"abstract - {doc}";

            signatures.Add(new SignatureInformation
            {
                Label = sigLabel,
                Documentation = doc,
                Parameters = new Container<ParameterInformation>(parameters)
            });
        }

        return signatures;
    }

    /// <summary>
    /// Get signatures for type constructors.
    /// </summary>
    private static List<SignatureInformation> GetTypeConstructorSignatures(string typeName)
    {
        var signatures = new List<SignatureInformation>();
        var typeInfo = HlslTypeSystem.GetTypeInfo(typeName);

        if (typeInfo == null)
            return signatures;

        if (typeInfo.IsVector)
        {
            var scalarType = typeInfo.GetScalarTypeName();
            var count = typeInfo.Rows;

            // Single scalar broadcast
            signatures.Add(new SignatureInformation
            {
                Label = $"{typeName}({scalarType} value)",
                Documentation = $"Broadcasts scalar to all {count} components",
                Parameters = new Container<ParameterInformation>(new ParameterInformation
                {
                    Label = $"{scalarType} value",
                    Documentation = "Value to broadcast"
                })
            });

            // Individual components
            var components = string.Join(", ", Enumerable.Range(0, count).Select(i =>
                $"{scalarType} {(char)('x' + i)}"));
            signatures.Add(new SignatureInformation
            {
                Label = $"{typeName}({components})",
                Documentation = $"Constructs {typeName} from {count} individual values",
                Parameters = new Container<ParameterInformation>(
                    Enumerable.Range(0, count).Select(i => new ParameterInformation
                    {
                        Label = $"{scalarType} {(char)('x' + i)}",
                        Documentation = $"Component {(char)('x' + i)}"
                    }))
            });
        }
        else if (typeInfo.IsMatrix)
        {
            var scalarType = typeInfo.GetScalarTypeName();

            // Single scalar
            signatures.Add(new SignatureInformation
            {
                Label = $"{typeName}({scalarType} value)",
                Documentation = "Creates matrix with value on diagonal, 0 elsewhere",
                Parameters = new Container<ParameterInformation>(new ParameterInformation
                {
                    Label = $"{scalarType} value",
                    Documentation = "Diagonal value"
                })
            });

            // Row vectors
            var rowType = $"{scalarType}{typeInfo.Cols}";
            var rowParams = string.Join(", ", Enumerable.Range(0, typeInfo.Rows).Select(i => $"{rowType} row{i}"));
            signatures.Add(new SignatureInformation
            {
                Label = $"{typeName}({rowParams})",
                Documentation = $"Constructs matrix from {typeInfo.Rows} row vectors",
                Parameters = new Container<ParameterInformation>(
                    Enumerable.Range(0, typeInfo.Rows).Select(i => new ParameterInformation
                    {
                        Label = $"{rowType} row{i}",
                        Documentation = $"Row {i}"
                    }))
            });
        }

        return signatures;
    }

    /// <summary>
    /// Parse parameter names from a signature string.
    /// </summary>
    private static List<string> ParseParameters(string signature)
    {
        var start = signature.IndexOf('(');
        var end = signature.LastIndexOf(')');
        if (start < 0 || end < 0 || end <= start)
            return new List<string>();

        var paramString = signature.Substring(start + 1, end - start - 1);
        if (string.IsNullOrWhiteSpace(paramString))
            return new List<string>();

        return paramString.Split(',').Select(p => p.Trim()).ToList();
    }

    protected override SignatureHelpRegistrationOptions CreateRegistrationOptions(
        SignatureHelpCapability capability,
        ClientCapabilities clientCapabilities)
    {
        return new SignatureHelpRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForPattern("**/*.sdsl"),
            TriggerCharacters = new Container<string>("(", ","),
            RetriggerCharacters = new Container<string>(",")
        };
    }
}
