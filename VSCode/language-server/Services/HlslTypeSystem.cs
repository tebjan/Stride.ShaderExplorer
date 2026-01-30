namespace StrideShaderLanguageServer.Services;

/// <summary>
/// Comprehensive HLSL/SDSL type system based on official Microsoft HLSL specification.
/// Handles type inference, implicit conversions, and swizzle operations.
/// </summary>
public static class HlslTypeSystem
{
    #region Scalar Type Hierarchy

    /// <summary>
    /// Scalar types ordered by promotion priority (higher = preferred in mixed operations).
    /// Based on HLSL spec: Double > Float > Half > UInt > Int > Bool
    /// </summary>
    public enum ScalarType
    {
        Unknown = -1,
        Bool = 0,
        Int = 1,
        UInt = 2,
        Half = 3,
        Float = 4,
        Double = 5
    }

    /// <summary>
    /// Represents type information for any HLSL type (scalar, vector, matrix, or special).
    /// </summary>
    public record TypeInfo(
        string Name,
        ScalarType BaseType,
        int Rows,      // 1 for scalar, 1-4 for vectors, 1-4 for matrix rows
        int Cols,      // 1 for scalar/vector, 1-4 for matrix cols
        bool IsMatrix  // True if this is a matrix type
    )
    {
        public bool IsScalar => Rows == 1 && Cols == 1 && !IsMatrix;
        public bool IsVector => Rows > 1 && Cols == 1 && !IsMatrix;
        public int ComponentCount => Rows * Cols;

        /// <summary>
        /// Get the scalar base type name (e.g., "float" from "float3").
        /// </summary>
        public string GetScalarTypeName() => BaseType switch
        {
            ScalarType.Bool => "bool",
            ScalarType.Int => "int",
            ScalarType.UInt => "uint",
            ScalarType.Half => "half",
            ScalarType.Float => "float",
            ScalarType.Double => "double",
            _ => "unknown"
        };
    }

    #endregion

    #region Type Registry

    /// <summary>
    /// Complete registry of all HLSL types with their properties.
    /// </summary>
    private static readonly Dictionary<string, TypeInfo> TypeRegistry = new(StringComparer.OrdinalIgnoreCase)
    {
        // Scalars
        ["bool"] = new("bool", ScalarType.Bool, 1, 1, false),
        ["int"] = new("int", ScalarType.Int, 1, 1, false),
        ["uint"] = new("uint", ScalarType.UInt, 1, 1, false),
        ["dword"] = new("dword", ScalarType.UInt, 1, 1, false),
        ["half"] = new("half", ScalarType.Half, 1, 1, false),
        ["float"] = new("float", ScalarType.Float, 1, 1, false),
        ["double"] = new("double", ScalarType.Double, 1, 1, false),

        // Minimum precision types (map to their base type for conversion purposes)
        ["min16float"] = new("min16float", ScalarType.Half, 1, 1, false),
        ["min10float"] = new("min10float", ScalarType.Half, 1, 1, false),
        ["min16int"] = new("min16int", ScalarType.Int, 1, 1, false),
        ["min16uint"] = new("min16uint", ScalarType.UInt, 1, 1, false),

        // Bool vectors
        ["bool1"] = new("bool1", ScalarType.Bool, 1, 1, false),
        ["bool2"] = new("bool2", ScalarType.Bool, 2, 1, false),
        ["bool3"] = new("bool3", ScalarType.Bool, 3, 1, false),
        ["bool4"] = new("bool4", ScalarType.Bool, 4, 1, false),

        // Int vectors
        ["int1"] = new("int1", ScalarType.Int, 1, 1, false),
        ["int2"] = new("int2", ScalarType.Int, 2, 1, false),
        ["int3"] = new("int3", ScalarType.Int, 3, 1, false),
        ["int4"] = new("int4", ScalarType.Int, 4, 1, false),

        // UInt vectors
        ["uint1"] = new("uint1", ScalarType.UInt, 1, 1, false),
        ["uint2"] = new("uint2", ScalarType.UInt, 2, 1, false),
        ["uint3"] = new("uint3", ScalarType.UInt, 3, 1, false),
        ["uint4"] = new("uint4", ScalarType.UInt, 4, 1, false),

        // Half vectors
        ["half1"] = new("half1", ScalarType.Half, 1, 1, false),
        ["half2"] = new("half2", ScalarType.Half, 2, 1, false),
        ["half3"] = new("half3", ScalarType.Half, 3, 1, false),
        ["half4"] = new("half4", ScalarType.Half, 4, 1, false),

        // Float vectors
        ["float1"] = new("float1", ScalarType.Float, 1, 1, false),
        ["float2"] = new("float2", ScalarType.Float, 2, 1, false),
        ["float3"] = new("float3", ScalarType.Float, 3, 1, false),
        ["float4"] = new("float4", ScalarType.Float, 4, 1, false),

        // Double vectors
        ["double1"] = new("double1", ScalarType.Double, 1, 1, false),
        ["double2"] = new("double2", ScalarType.Double, 2, 1, false),
        ["double3"] = new("double3", ScalarType.Double, 3, 1, false),
        ["double4"] = new("double4", ScalarType.Double, 4, 1, false),

        // Float matrices (most common)
        ["float1x1"] = new("float1x1", ScalarType.Float, 1, 1, true),
        ["float1x2"] = new("float1x2", ScalarType.Float, 1, 2, true),
        ["float1x3"] = new("float1x3", ScalarType.Float, 1, 3, true),
        ["float1x4"] = new("float1x4", ScalarType.Float, 1, 4, true),
        ["float2x1"] = new("float2x1", ScalarType.Float, 2, 1, true),
        ["float2x2"] = new("float2x2", ScalarType.Float, 2, 2, true),
        ["float2x3"] = new("float2x3", ScalarType.Float, 2, 3, true),
        ["float2x4"] = new("float2x4", ScalarType.Float, 2, 4, true),
        ["float3x1"] = new("float3x1", ScalarType.Float, 3, 1, true),
        ["float3x2"] = new("float3x2", ScalarType.Float, 3, 2, true),
        ["float3x3"] = new("float3x3", ScalarType.Float, 3, 3, true),
        ["float3x4"] = new("float3x4", ScalarType.Float, 3, 4, true),
        ["float4x1"] = new("float4x1", ScalarType.Float, 4, 1, true),
        ["float4x2"] = new("float4x2", ScalarType.Float, 4, 2, true),
        ["float4x3"] = new("float4x3", ScalarType.Float, 4, 3, true),
        ["float4x4"] = new("float4x4", ScalarType.Float, 4, 4, true),

        // Int matrices
        ["int1x1"] = new("int1x1", ScalarType.Int, 1, 1, true),
        ["int2x2"] = new("int2x2", ScalarType.Int, 2, 2, true),
        ["int3x3"] = new("int3x3", ScalarType.Int, 3, 3, true),
        ["int4x4"] = new("int4x4", ScalarType.Int, 4, 4, true),

        // Double matrices (less common but valid)
        ["double2x2"] = new("double2x2", ScalarType.Double, 2, 2, true),
        ["double3x3"] = new("double3x3", ScalarType.Double, 3, 3, true),
        ["double4x4"] = new("double4x4", ScalarType.Double, 4, 4, true),

        // Stride/HLSL color types (aliases for float vectors)
        ["Color3"] = new("Color3", ScalarType.Float, 3, 1, false),
        ["Color4"] = new("Color4", ScalarType.Float, 4, 1, false),
        ["Color"] = new("Color", ScalarType.Float, 4, 1, false),
    };

    /// <summary>
    /// Texture and sampler types (no numeric conversion, special handling).
    /// </summary>
    private static readonly HashSet<string> TextureTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Texture1D", "Texture1DArray", "Texture2D", "Texture2DArray",
        "Texture2DMS", "Texture2DMSArray", "Texture3D",
        "TextureCube", "TextureCubeArray",
        "RWTexture1D", "RWTexture2D", "RWTexture2DArray", "RWTexture3D",
        "Buffer", "ByteAddressBuffer", "StructuredBuffer",
        "ConsumeStructuredBuffer", "AppendStructuredBuffer",
        "RWBuffer", "RWByteAddressBuffer", "RWStructuredBuffer"
    };

    /// <summary>
    /// Sampler types.
    /// </summary>
    private static readonly HashSet<string> SamplerTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "SamplerState", "SamplerComparisonState", "sampler", "sampler1D",
        "sampler2D", "sampler3D", "samplerCUBE"
    };

    #endregion

    #region Swizzle Definitions

    /// <summary>
    /// Valid swizzle sets - cannot mix between sets (e.g., .xg is invalid).
    /// </summary>
    private static readonly char[][] SwizzleSets =
    {
        new[] { 'x', 'y', 'z', 'w' },  // Position
        new[] { 'r', 'g', 'b', 'a' },  // Color
        new[] { 's', 't', 'p', 'q' }   // Texture coordinates
    };

    /// <summary>
    /// Maps swizzle component to its index (0-3).
    /// </summary>
    private static readonly Dictionary<char, int> SwizzleIndex = new()
    {
        ['x'] = 0, ['y'] = 1, ['z'] = 2, ['w'] = 3,
        ['r'] = 0, ['g'] = 1, ['b'] = 2, ['a'] = 3,
        ['s'] = 0, ['t'] = 1, ['p'] = 2, ['q'] = 3
    };

    #endregion

    #region Stride Stream Types

    /// <summary>
    /// Stride special stream types with their mutability.
    /// </summary>
    private static readonly Dictionary<string, bool> StreamTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Streams"] = true,    // Mutable
        ["Input"] = false,     // Immutable
        ["Input2"] = false,    // Immutable
        ["Output"] = true,     // Mutable
        ["Constants"] = false  // Immutable (read-only)
    };

    #endregion

    #region Public API

    /// <summary>
    /// Get type information for a type name. Returns null for unknown types.
    /// </summary>
    public static TypeInfo? GetTypeInfo(string typeName)
    {
        if (string.IsNullOrEmpty(typeName))
            return null;

        // Normalize: remove generic parameters
        var idx = typeName.IndexOf('<');
        if (idx > 0)
            typeName = typeName.Substring(0, idx);

        typeName = typeName.Trim();

        if (TypeRegistry.TryGetValue(typeName, out var info))
            return info;

        // Check for dynamically parsed matrix types (e.g., "matrix<float, 3, 3>")
        // For now, return null for unsupported types
        return null;
    }

    /// <summary>
    /// Check if a type conversion is valid and determine if it's implicit or requires a warning.
    /// </summary>
    public static ConversionResult CheckConversion(string fromType, string toType)
    {
        if (string.IsNullOrEmpty(fromType) || string.IsNullOrEmpty(toType))
            return new ConversionResult(true, true, null); // Unknown types - allow

        // Same type is always valid
        if (fromType.Equals(toType, StringComparison.OrdinalIgnoreCase))
            return new ConversionResult(true, true, null);

        var fromInfo = GetTypeInfo(fromType);
        var toInfo = GetTypeInfo(toType);

        // Unknown types - allow (might be user-defined structs)
        if (fromInfo == null || toInfo == null)
            return new ConversionResult(true, true, null);

        // Texture/Sampler types - no conversion
        if (IsTextureType(fromType) || IsTextureType(toType) ||
            IsSamplerType(fromType) || IsSamplerType(toType))
        {
            if (fromType.Equals(toType, StringComparison.OrdinalIgnoreCase))
                return new ConversionResult(true, true, null);
            return new ConversionResult(false, false, $"Cannot convert {fromType} to {toType}");
        }

        return CheckNumericConversion(fromInfo, toInfo);
    }

    /// <summary>
    /// Infer the result type of a swizzle operation.
    /// Returns null if the swizzle is invalid.
    /// </summary>
    public static SwizzleResult InferSwizzleType(string baseType, string swizzle)
    {
        if (string.IsNullOrEmpty(baseType) || string.IsNullOrEmpty(swizzle))
            return new SwizzleResult(null, "Invalid swizzle");

        var baseInfo = GetTypeInfo(baseType);
        if (baseInfo == null)
            return new SwizzleResult(null, "Unknown base type");

        // Only vectors and matrices support swizzling
        if (baseInfo.IsScalar && baseInfo.Rows == 1)
            return new SwizzleResult(null, "Cannot swizzle a scalar");

        // Validate swizzle characters
        var validationResult = ValidateSwizzle(swizzle, baseInfo.Rows);
        if (validationResult != null)
            return new SwizzleResult(null, validationResult);

        // Result type based on swizzle length
        var resultType = swizzle.Length switch
        {
            1 => baseInfo.GetScalarTypeName(),
            2 => $"{baseInfo.GetScalarTypeName()}2",
            3 => $"{baseInfo.GetScalarTypeName()}3",
            4 => $"{baseInfo.GetScalarTypeName()}4",
            _ => null
        };

        return new SwizzleResult(resultType, null);
    }

    /// <summary>
    /// Infer the result type of a binary operation.
    /// </summary>
    public static string? InferBinaryResultType(string leftType, string rightType, string op)
    {
        var leftInfo = GetTypeInfo(leftType);
        var rightInfo = GetTypeInfo(rightType);

        // Unknown types
        if (leftInfo == null) return rightType;
        if (rightInfo == null) return leftType;

        // Comparison/logical operators return bool
        if (IsComparisonOperator(op) || IsLogicalOperator(op))
        {
            // Vector comparison returns bool vector
            var maxRows = Math.Max(leftInfo.Rows, rightInfo.Rows);
            return maxRows == 1 ? "bool" : $"bool{maxRows}";
        }

        // For matrix multiplication, special rules apply
        if (op == "*" && (leftInfo.IsMatrix || rightInfo.IsMatrix))
        {
            return InferMatrixMultiplyType(leftInfo, rightInfo);
        }

        // For other operations, follow promotion rules
        return InferArithmeticResultType(leftInfo, rightInfo);
    }

    /// <summary>
    /// Check if a type is a Stride stream type.
    /// </summary>
    public static bool IsStreamType(string typeName) =>
        StreamTypes.ContainsKey(typeName);

    /// <summary>
    /// Check if a stream type is mutable.
    /// </summary>
    public static bool IsStreamMutable(string typeName) =>
        StreamTypes.TryGetValue(typeName, out var mutable) && mutable;

    /// <summary>
    /// Check if a type is a texture type.
    /// </summary>
    public static bool IsTextureType(string typeName) =>
        TextureTypes.Contains(typeName);

    /// <summary>
    /// Check if a type is a sampler type.
    /// </summary>
    public static bool IsSamplerType(string typeName) =>
        SamplerTypes.Contains(typeName);

    /// <summary>
    /// Get all known type names (for completion).
    /// </summary>
    public static IEnumerable<string> GetAllTypeNames() =>
        TypeRegistry.Keys.Concat(TextureTypes).Concat(SamplerTypes);

    #endregion

    #region Private Helpers

    private static ConversionResult CheckNumericConversion(TypeInfo from, TypeInfo to)
    {
        // Scalar promotion is always allowed (implicit)
        if (from.IsScalar && from.Rows == 1)
        {
            // Scalar to scalar
            if (to.IsScalar && to.Rows == 1)
            {
                return CheckScalarToScalarConversion(from.BaseType, to.BaseType);
            }

            // Scalar to vector/matrix (broadcasting) - always allowed
            return new ConversionResult(true, true, null);
        }

        // Vector conversions
        if (from.IsVector && !from.IsMatrix)
        {
            // Vector to scalar - truncation warning
            if (to.IsScalar && to.Rows == 1)
            {
                return new ConversionResult(true, false,
                    $"Implicit truncation from {from.Name} to {to.Name}");
            }

            // Vector to vector
            if (to.IsVector && !to.IsMatrix)
            {
                if (from.Rows > to.Rows)
                {
                    // Truncation - warning
                    return new ConversionResult(true, false,
                        $"Implicit truncation from {from.Name} to {to.Name}");
                }
                else if (from.Rows < to.Rows)
                {
                    // Extension - error
                    return new ConversionResult(false, false,
                        $"Cannot implicitly extend {from.Name} to {to.Name}");
                }
                else
                {
                    // Same dimension - check base type
                    return CheckScalarToScalarConversion(from.BaseType, to.BaseType);
                }
            }

            // Vector to matrix - not allowed without explicit cast
            if (to.IsMatrix)
            {
                return new ConversionResult(false, false,
                    $"Cannot implicitly convert {from.Name} to matrix {to.Name}");
            }
        }

        // Matrix conversions
        if (from.IsMatrix)
        {
            // Matrix to scalar - truncation
            if (to.IsScalar && to.Rows == 1)
            {
                return new ConversionResult(true, false,
                    $"Implicit truncation from matrix {from.Name} to {to.Name}");
            }

            // Matrix to vector - truncation
            if (to.IsVector && !to.IsMatrix)
            {
                return new ConversionResult(true, false,
                    $"Implicit truncation from matrix {from.Name} to {to.Name}");
            }

            // Matrix to matrix
            if (to.IsMatrix)
            {
                if (from.Rows > to.Rows || from.Cols > to.Cols)
                {
                    // Truncation - warning
                    return new ConversionResult(true, false,
                        $"Implicit truncation from {from.Name} to {to.Name} (top-left submatrix)");
                }
                else if (from.Rows < to.Rows || from.Cols < to.Cols)
                {
                    // Extension - error
                    return new ConversionResult(false, false,
                        $"Cannot implicitly extend matrix {from.Name} to {to.Name}");
                }
                else
                {
                    // Same dimensions - check base type
                    return CheckScalarToScalarConversion(from.BaseType, to.BaseType);
                }
            }
        }

        // Default: allow with warning for unknown cases
        return new ConversionResult(true, true, null);
    }

    private static ConversionResult CheckScalarToScalarConversion(ScalarType from, ScalarType to)
    {
        if (from == to)
            return new ConversionResult(true, true, null);

        // Promotion (to higher precision) is implicit
        if ((int)from < (int)to)
            return new ConversionResult(true, true, null);

        // Demotion (to lower precision) generates warning
        // E.g., float → int, double → float
        var fromName = from.ToString().ToLower();
        var toName = to.ToString().ToLower();

        if (from == ScalarType.Float && to == ScalarType.Int)
            return new ConversionResult(true, false, $"Implicit conversion from {fromName} to {toName} may lose precision");

        if (from == ScalarType.Double && (to == ScalarType.Float || to == ScalarType.Half))
            return new ConversionResult(true, false, $"Implicit conversion from {fromName} to {toName} may lose precision");

        if (from == ScalarType.Float && to == ScalarType.Half)
            return new ConversionResult(true, false, $"Implicit conversion from {fromName} to {toName} may lose precision");

        // Signed/unsigned conversion
        if ((from == ScalarType.Int && to == ScalarType.UInt) ||
            (from == ScalarType.UInt && to == ScalarType.Int))
            return new ConversionResult(true, false, $"Implicit signed/unsigned conversion from {fromName} to {toName}");

        return new ConversionResult(true, true, null);
    }

    private static string? ValidateSwizzle(string swizzle, int maxComponents)
    {
        if (swizzle.Length == 0 || swizzle.Length > 4)
            return "Swizzle must be 1-4 components";

        int? swizzleSetIndex = null;

        foreach (var c in swizzle.ToLowerInvariant())
        {
            // Find which set this character belongs to
            int? foundSet = null;
            for (int i = 0; i < SwizzleSets.Length; i++)
            {
                if (SwizzleSets[i].Contains(c))
                {
                    foundSet = i;
                    break;
                }
            }

            if (foundSet == null)
                return $"Invalid swizzle component '{c}'";

            // Check for mixed swizzle sets
            if (swizzleSetIndex == null)
                swizzleSetIndex = foundSet;
            else if (swizzleSetIndex != foundSet)
                return "Cannot mix swizzle component sets (e.g., .xg is invalid)";

            // Check component index bounds
            if (SwizzleIndex.TryGetValue(c, out var idx) && idx >= maxComponents)
                return $"Swizzle component '{c}' out of bounds for type with {maxComponents} components";
        }

        return null; // Valid
    }

    private static string? InferMatrixMultiplyType(TypeInfo left, TypeInfo right)
    {
        // Matrix × Matrix: [M×N] × [N×P] = [M×P]
        if (left.IsMatrix && right.IsMatrix)
        {
            if (left.Cols != right.Rows)
                return null; // Incompatible dimensions

            var baseType = (ScalarType)Math.Max((int)left.BaseType, (int)right.BaseType);
            var scalarName = left.BaseType switch
            {
                ScalarType.Double => "double",
                ScalarType.Float => "float",
                ScalarType.Half => "half",
                _ => "float"
            };

            if (left.Rows == 1 && right.Cols == 1)
                return scalarName; // Results in scalar
            if (left.Rows == 1)
                return $"{scalarName}{right.Cols}"; // Row vector
            if (right.Cols == 1)
                return $"{scalarName}{left.Rows}"; // Column vector

            return $"{scalarName}{left.Rows}x{right.Cols}";
        }

        // Vector × Matrix or Matrix × Vector
        if (left.IsMatrix && !right.IsMatrix)
        {
            // Matrix × Vector: [M×N] × [N] = [M]
            var scalarName = left.GetScalarTypeName();
            return left.Rows == 1 ? scalarName : $"{scalarName}{left.Rows}";
        }

        if (!left.IsMatrix && right.IsMatrix)
        {
            // Vector × Matrix: [M] × [M×N] = [N]
            var scalarName = right.GetScalarTypeName();
            return right.Cols == 1 ? scalarName : $"{scalarName}{right.Cols}";
        }

        return null;
    }

    private static string? InferArithmeticResultType(TypeInfo left, TypeInfo right)
    {
        // Get the promoted scalar type
        var promotedScalar = (ScalarType)Math.Max((int)left.BaseType, (int)right.BaseType);
        var scalarName = promotedScalar switch
        {
            ScalarType.Bool => "bool",
            ScalarType.Int => "int",
            ScalarType.UInt => "uint",
            ScalarType.Half => "half",
            ScalarType.Float => "float",
            ScalarType.Double => "double",
            _ => "float"
        };

        // Both scalars
        if (left.IsScalar && left.Rows == 1 && right.IsScalar && right.Rows == 1)
            return scalarName;

        // One scalar, one vector - broadcast to vector size
        if ((left.IsScalar && left.Rows == 1) || (right.IsScalar && right.Rows == 1))
        {
            var vectorDim = Math.Max(left.Rows, right.Rows);
            return vectorDim == 1 ? scalarName : $"{scalarName}{vectorDim}";
        }

        // Both vectors - use minimum dimension (truncation semantics)
        if (left.IsVector && right.IsVector)
        {
            var dim = Math.Min(left.Rows, right.Rows);
            return dim == 1 ? scalarName : $"{scalarName}{dim}";
        }

        // Matrix involved - complex, return the matrix type
        if (left.IsMatrix || right.IsMatrix)
        {
            var matrixInfo = left.IsMatrix ? left : right;
            return $"{scalarName}{matrixInfo.Rows}x{matrixInfo.Cols}";
        }

        return scalarName;
    }

    private static bool IsComparisonOperator(string op) =>
        op is "<" or "<=" or ">" or ">=" or "==" or "!=" or "eq" or "ne" or "lt" or "le" or "gt" or "ge";

    private static bool IsLogicalOperator(string op) =>
        op is "&&" or "||" or "!" or "and" or "or" or "not";

    #endregion
}

/// <summary>
/// Result of a type conversion check.
/// </summary>
public record ConversionResult(
    bool Allowed,     // Whether the conversion is valid at all
    bool IsImplicit,  // Whether it's implicit (true) or requires explicit cast/generates warning (false)
    string? Warning   // Warning message if applicable
);

/// <summary>
/// Result of swizzle type inference.
/// </summary>
public record SwizzleResult(
    string? ResultType,  // The resulting type, or null if invalid
    string? Error        // Error message if invalid
);
