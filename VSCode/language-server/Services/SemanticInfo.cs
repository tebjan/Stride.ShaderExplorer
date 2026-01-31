namespace StrideShaderLanguageServer.Services;

/// <summary>
/// Provides documentation and validation for HLSL/SDSL shader semantics.
/// </summary>
public static class SemanticInfo
{
    /// <summary>
    /// Dictionary of all known semantics with their documentation.
    /// Uses case-insensitive comparison for lookups.
    /// </summary>
    public static readonly Dictionary<string, SemanticDocumentation> Semantics = new(StringComparer.OrdinalIgnoreCase)
    {
        // ═══════════════════════════════════════════════════════════════════════════════
        // System Value Semantics (SV_*) - GPU-specific values
        // ═══════════════════════════════════════════════════════════════════════════════

        // Position semantics
        ["SV_Position"] = new("Vertex position in homogeneous clip space (x, y, z, w). After vertex shader, this is the transformed position used for rasterization.", ShaderStage.VertexOutput | ShaderStage.PixelInput),

        // Render target outputs
        ["SV_Target"] = new("Render target output color. Equivalent to SV_Target0.", ShaderStage.PixelOutput),
        ["SV_Target0"] = new("Render target 0 output color (primary render target).", ShaderStage.PixelOutput),
        ["SV_Target1"] = new("Render target 1 output color (MRT - Multiple Render Targets).", ShaderStage.PixelOutput),
        ["SV_Target2"] = new("Render target 2 output color (MRT).", ShaderStage.PixelOutput),
        ["SV_Target3"] = new("Render target 3 output color (MRT).", ShaderStage.PixelOutput),
        ["SV_Target4"] = new("Render target 4 output color (MRT).", ShaderStage.PixelOutput),
        ["SV_Target5"] = new("Render target 5 output color (MRT).", ShaderStage.PixelOutput),
        ["SV_Target6"] = new("Render target 6 output color (MRT).", ShaderStage.PixelOutput),
        ["SV_Target7"] = new("Render target 7 output color (MRT).", ShaderStage.PixelOutput),

        // Depth output
        ["SV_Depth"] = new("Depth buffer output value. Allows pixel shader to override interpolated depth.", ShaderStage.PixelOutput),
        ["SV_DepthGreater"] = new("Depth value that is >= interpolated depth. Enables early-Z optimization.", ShaderStage.PixelOutput),
        ["SV_DepthLessEqual"] = new("Depth value that is <= interpolated depth. Enables early-Z optimization.", ShaderStage.PixelOutput),

        // Vertex/Instance IDs
        ["SV_VertexID"] = new("Zero-based index of the vertex in the vertex buffer.", ShaderStage.VertexInput),
        ["SV_InstanceID"] = new("Zero-based instance index for instanced rendering. Use for per-instance transformations.", ShaderStage.VertexInput),

        // Primitive IDs
        ["SV_PrimitiveID"] = new("Zero-based primitive (triangle/line/point) index within the draw call.", ShaderStage.GeometryInput | ShaderStage.PixelInput | ShaderStage.HullInput | ShaderStage.DomainInput),

        // Face orientation
        ["SV_IsFrontFace"] = new("True if the primitive is front-facing, false if back-facing. Useful for two-sided rendering.", ShaderStage.PixelInput),

        // Coverage and sample info
        ["SV_Coverage"] = new("Bitmask of which samples are covered by the primitive (MSAA).", ShaderStage.PixelInput | ShaderStage.PixelOutput),
        ["SV_SampleIndex"] = new("Sample index when running per-sample (MSAA). Forces per-sample execution.", ShaderStage.PixelInput),

        // Stencil output
        ["SV_StencilRef"] = new("Stencil reference value output from pixel shader.", ShaderStage.PixelOutput),

        // Clip/Cull distances
        ["SV_ClipDistance"] = new("Per-vertex clip distance. Primitives are clipped against planes defined by these values.", ShaderStage.VertexOutput | ShaderStage.PixelInput),
        ["SV_ClipDistance0"] = new("Clip distance for plane 0.", ShaderStage.VertexOutput | ShaderStage.PixelInput),
        ["SV_ClipDistance1"] = new("Clip distance for plane 1.", ShaderStage.VertexOutput | ShaderStage.PixelInput),
        ["SV_CullDistance"] = new("Per-vertex cull distance. Primitives are culled (discarded) if all vertices have negative values.", ShaderStage.VertexOutput | ShaderStage.PixelInput),
        ["SV_CullDistance0"] = new("Cull distance for plane 0.", ShaderStage.VertexOutput | ShaderStage.PixelInput),
        ["SV_CullDistance1"] = new("Cull distance for plane 1.", ShaderStage.VertexOutput | ShaderStage.PixelInput),

        // Render target array index
        ["SV_RenderTargetArrayIndex"] = new("Index into a render target array. Used with texture arrays or cubemap faces.", ShaderStage.GeometryOutput | ShaderStage.PixelInput),
        ["SV_ViewportArrayIndex"] = new("Index of the viewport to use. Enables rendering to multiple viewports.", ShaderStage.GeometryOutput | ShaderStage.PixelInput),

        // Tessellation semantics
        ["SV_TessFactor"] = new("Tessellation factors for edges. Controls subdivision level.", ShaderStage.HullOutput),
        ["SV_InsideTessFactor"] = new("Tessellation factor for the interior of the patch.", ShaderStage.HullOutput),
        ["SV_DomainLocation"] = new("Parametric coordinates (u, v, w) within the tessellated patch.", ShaderStage.DomainInput),
        ["SV_OutputControlPointID"] = new("Index of the control point being processed in hull shader.", ShaderStage.HullInput),

        // Geometry shader
        ["SV_GSInstanceID"] = new("Instance ID when using geometry shader instancing.", ShaderStage.GeometryInput),

        // ═══════════════════════════════════════════════════════════════════════════════
        // Compute Shader Semantics
        // ═══════════════════════════════════════════════════════════════════════════════

        ["SV_DispatchThreadID"] = new("Global thread ID across all thread groups (uint3). Unique ID for each thread in the dispatch.", ShaderStage.ComputeInput),
        ["SV_GroupID"] = new("Thread group ID (uint3). Identifies which group this thread belongs to.", ShaderStage.ComputeInput),
        ["SV_GroupIndex"] = new("Flattened index within the thread group (uint). Range: 0 to (X*Y*Z)-1.", ShaderStage.ComputeInput),
        ["SV_GroupThreadID"] = new("Thread ID within the group (uint3). Local position in the group.", ShaderStage.ComputeInput),

        // ═══════════════════════════════════════════════════════════════════════════════
        // Legacy Semantics (Vertex Input from Mesh Data)
        // ═══════════════════════════════════════════════════════════════════════════════

        // Position
        ["POSITION"] = new("Vertex position in object/model space. Primary vertex position from mesh data.", ShaderStage.VertexInput),
        ["POSITION0"] = new("Primary vertex position (same as POSITION).", ShaderStage.VertexInput),
        ["POSITION1"] = new("Secondary position (e.g., for blend shapes/morphs).", ShaderStage.VertexInput),

        // Normals and tangents
        ["NORMAL"] = new("Vertex normal vector. Used for lighting calculations.", ShaderStage.VertexInput | ShaderStage.Interpolated),
        ["NORMAL0"] = new("Primary vertex normal.", ShaderStage.VertexInput | ShaderStage.Interpolated),
        ["TANGENT"] = new("Vertex tangent vector. Used with normal mapping for TBN matrix.", ShaderStage.VertexInput | ShaderStage.Interpolated),
        ["TANGENT0"] = new("Primary vertex tangent.", ShaderStage.VertexInput | ShaderStage.Interpolated),
        ["BINORMAL"] = new("Vertex binormal/bitangent. Can be computed as cross(Normal, Tangent) * handedness.", ShaderStage.VertexInput | ShaderStage.Interpolated),
        ["BINORMAL0"] = new("Primary vertex binormal.", ShaderStage.VertexInput | ShaderStage.Interpolated),

        // Vertex colors
        ["COLOR"] = new("Vertex color (float4). Often used for tinting or vertex painting.", ShaderStage.VertexInput | ShaderStage.Interpolated),
        ["COLOR0"] = new("Primary vertex color.", ShaderStage.VertexInput | ShaderStage.Interpolated),
        ["COLOR1"] = new("Secondary vertex color.", ShaderStage.VertexInput | ShaderStage.Interpolated),

        // Texture coordinates
        ["TEXCOORD"] = new("Texture coordinates (UV). Default is TEXCOORD0.", ShaderStage.VertexInput | ShaderStage.Interpolated),
        ["TEXCOORD0"] = new("Primary texture coordinates (UV0). Main diffuse/albedo mapping.", ShaderStage.VertexInput | ShaderStage.Interpolated),
        ["TEXCOORD1"] = new("Secondary texture coordinates (UV1). Often lightmap UVs.", ShaderStage.VertexInput | ShaderStage.Interpolated),
        ["TEXCOORD2"] = new("Texture coordinates channel 2.", ShaderStage.VertexInput | ShaderStage.Interpolated),
        ["TEXCOORD3"] = new("Texture coordinates channel 3.", ShaderStage.VertexInput | ShaderStage.Interpolated),
        ["TEXCOORD4"] = new("Texture coordinates channel 4.", ShaderStage.VertexInput | ShaderStage.Interpolated),
        ["TEXCOORD5"] = new("Texture coordinates channel 5.", ShaderStage.VertexInput | ShaderStage.Interpolated),
        ["TEXCOORD6"] = new("Texture coordinates channel 6.", ShaderStage.VertexInput | ShaderStage.Interpolated),
        ["TEXCOORD7"] = new("Texture coordinates channel 7.", ShaderStage.VertexInput | ShaderStage.Interpolated),
        ["TEXCOORD8"] = new("Texture coordinates channel 8.", ShaderStage.VertexInput | ShaderStage.Interpolated),
        ["TEXCOORD9"] = new("Texture coordinates channel 9.", ShaderStage.VertexInput | ShaderStage.Interpolated),

        // Blend weights/indices for skeletal animation
        ["BLENDWEIGHT"] = new("Blend weights for skeletal animation (up to 4 bones).", ShaderStage.VertexInput),
        ["BLENDWEIGHT0"] = new("Primary blend weights.", ShaderStage.VertexInput),
        ["BLENDINDICES"] = new("Bone indices for skeletal animation (up to 4 bones).", ShaderStage.VertexInput),
        ["BLENDINDICES0"] = new("Primary bone indices.", ShaderStage.VertexInput),

        // Point size
        ["PSIZE"] = new("Point sprite size. Controls the size of point primitives.", ShaderStage.VertexInput | ShaderStage.VertexOutput),
        ["PSIZE0"] = new("Primary point size.", ShaderStage.VertexInput | ShaderStage.VertexOutput),

        // Fog (legacy)
        ["FOG"] = new("Fog factor (legacy). Distance-based fog interpolation value.", ShaderStage.VertexOutput | ShaderStage.Interpolated),

        // Depth (output)
        ["DEPTH"] = new("Depth value output (legacy, prefer SV_Depth).", ShaderStage.PixelOutput),

        // ═══════════════════════════════════════════════════════════════════════════════
        // VR/Stereo Rendering
        // ═══════════════════════════════════════════════════════════════════════════════

        ["SV_ViewID"] = new("View ID for VR stereo rendering. 0 = left eye, 1 = right eye.", ShaderStage.VertexInput | ShaderStage.PixelInput),
    };

    /// <summary>
    /// Get the documentation string for a semantic.
    /// </summary>
    /// <param name="semantic">The semantic name (e.g., "SV_Position", "TEXCOORD0")</param>
    /// <returns>Documentation string, or null if semantic is unknown</returns>
    public static string? GetDocumentation(string semantic)
    {
        if (string.IsNullOrEmpty(semantic))
            return null;

        return Semantics.TryGetValue(semantic, out var info) ? info.Description : null;
    }

    /// <summary>
    /// Check if a semantic name is valid/known.
    /// </summary>
    public static bool IsValid(string semantic)
    {
        if (string.IsNullOrEmpty(semantic))
            return false;

        return Semantics.ContainsKey(semantic);
    }

    /// <summary>
    /// Get the valid shader stages for a semantic.
    /// </summary>
    public static ShaderStage GetValidStages(string semantic)
    {
        if (string.IsNullOrEmpty(semantic))
            return ShaderStage.None;

        return Semantics.TryGetValue(semantic, out var info) ? info.ValidStages : ShaderStage.None;
    }

    /// <summary>
    /// Find the closest matching semantic for typo correction.
    /// Uses Levenshtein distance for similarity matching.
    /// </summary>
    public static string? FindClosestSemantic(string misspelled)
    {
        if (string.IsNullOrEmpty(misspelled))
            return null;

        var upper = misspelled.ToUpperInvariant();
        string? best = null;
        int bestDistance = int.MaxValue;

        foreach (var semantic in Semantics.Keys)
        {
            var distance = LevenshteinDistance(upper, semantic.ToUpperInvariant());
            if (distance < bestDistance && distance <= 3) // Max 3 edits for suggestion
            {
                bestDistance = distance;
                best = semantic;
            }
        }

        return best;
    }

    /// <summary>
    /// Calculate Levenshtein edit distance between two strings.
    /// </summary>
    private static int LevenshteinDistance(string a, string b)
    {
        if (string.IsNullOrEmpty(a)) return b?.Length ?? 0;
        if (string.IsNullOrEmpty(b)) return a.Length;

        var costs = new int[b.Length + 1];
        for (int i = 0; i <= b.Length; i++)
            costs[i] = i;

        for (int i = 1; i <= a.Length; i++)
        {
            int prev = costs[0];
            costs[0] = i;

            for (int j = 1; j <= b.Length; j++)
            {
                int curr = costs[j];
                costs[j] = Math.Min(
                    Math.Min(costs[j - 1] + 1, costs[j] + 1),
                    prev + (a[i - 1] == b[j - 1] ? 0 : 1));
                prev = curr;
            }
        }

        return costs[b.Length];
    }
}

/// <summary>
/// Documentation for a single shader semantic.
/// </summary>
/// <param name="Description">Human-readable description of what the semantic does</param>
/// <param name="ValidStages">Bitflags indicating which shader stages can use this semantic</param>
public record SemanticDocumentation(string Description, ShaderStage ValidStages);

/// <summary>
/// Shader stages where semantics can be used.
/// </summary>
[Flags]
public enum ShaderStage
{
    None = 0,

    // Vertex shader
    VertexInput = 1 << 0,
    VertexOutput = 1 << 1,

    // Pixel/Fragment shader
    PixelInput = 1 << 2,
    PixelOutput = 1 << 3,

    // Geometry shader
    GeometryInput = 1 << 4,
    GeometryOutput = 1 << 5,

    // Hull shader (tessellation control)
    HullInput = 1 << 6,
    HullOutput = 1 << 7,

    // Domain shader (tessellation evaluation)
    DomainInput = 1 << 8,
    DomainOutput = 1 << 9,

    // Compute shader
    ComputeInput = 1 << 10,

    // Special: interpolated between stages
    Interpolated = 1 << 15,
}
