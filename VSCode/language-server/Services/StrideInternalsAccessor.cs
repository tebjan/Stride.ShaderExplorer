using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Stride.Shaders.Parser;

namespace StrideShaderLanguageServer.Services;

/// <summary>
/// Provides reflection-based access to internal Stride APIs.
/// Uses cached delegates for performance.
///
/// TODO: Submit PR to Stride to make these APIs public:
/// - ShaderMixinParser.ParseAndAnalyze()
/// - ModuleMixinInfo
/// - StrideParsingInfo
/// </summary>
public class StrideInternalsAccessor
{
    private readonly ILogger<StrideInternalsAccessor> _logger;
    private bool _initialized;
    private bool _available;

    // Cached reflection info
    private static Type? _shaderMixinParserType;
    private static MethodInfo? _parseAndAnalyzeMethod;
    private static Type? _moduleMixinInfoType;
    private static Type? _strideParsingInfoType;

    // Cached property accessors for ModuleMixinInfo
    private static PropertyInfo? _mixinInfoBaseTypesProperty;
    private static PropertyInfo? _mixinInfoMembersProperty;
    private static PropertyInfo? _mixinInfoNameProperty;

    public StrideInternalsAccessor(ILogger<StrideInternalsAccessor> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Initialize reflection access to internal APIs.
    /// </summary>
    public bool Initialize()
    {
        if (_initialized)
            return _available;

        _initialized = true;

        try
        {
            // Get the Stride.Shaders.Parser assembly
            var assembly = typeof(StrideShaderParser).Assembly;
            _logger.LogDebug("Accessing Stride.Shaders.Parser assembly: {Name}", assembly.FullName);

            // Try to find ShaderMixinParser type
            _shaderMixinParserType = assembly.GetType("Stride.Shaders.Parser.Mixins.ShaderMixinParser");
            if (_shaderMixinParserType == null)
            {
                _logger.LogWarning("ShaderMixinParser type not found - internal API access unavailable");
                return _available = false;
            }

            // Find ParseAndAnalyze method
            _parseAndAnalyzeMethod = _shaderMixinParserType.GetMethod(
                "ParseAndAnalyze",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);

            if (_parseAndAnalyzeMethod == null)
            {
                // Try alternative method names
                _parseAndAnalyzeMethod = _shaderMixinParserType.GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name.Contains("Parse") && m.Name.Contains("Analyze"));
            }

            // Find ModuleMixinInfo type
            _moduleMixinInfoType = assembly.GetType("Stride.Shaders.Parser.Mixins.ModuleMixinInfo");
            if (_moduleMixinInfoType != null)
            {
                _mixinInfoBaseTypesProperty = _moduleMixinInfoType.GetProperty("BaseMixins")
                    ?? _moduleMixinInfoType.GetProperty("BaseTypes")
                    ?? _moduleMixinInfoType.GetProperty("InheritanceList");
                _mixinInfoMembersProperty = _moduleMixinInfoType.GetProperty("Members")
                    ?? _moduleMixinInfoType.GetProperty("LocalMembers");
                _mixinInfoNameProperty = _moduleMixinInfoType.GetProperty("MixinName")
                    ?? _moduleMixinInfoType.GetProperty("Name");
            }

            // Find StrideParsingInfo type
            _strideParsingInfoType = assembly.GetType("Stride.Shaders.Parser.Mixins.StrideParsingInfo")
                ?? assembly.GetType("Stride.Shaders.Parser.StrideParsingInfo");

            LogAvailableInternals();

            _available = _shaderMixinParserType != null;
            return _available;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize Stride internals accessor");
            return _available = false;
        }
    }

    private void LogAvailableInternals()
    {
        _logger.LogInformation("Stride internal API access:");
        _logger.LogInformation("  ShaderMixinParser: {Status}", _shaderMixinParserType != null ? "Available" : "Not found");
        _logger.LogInformation("  ParseAndAnalyze method: {Status}", _parseAndAnalyzeMethod != null ? "Available" : "Not found");
        _logger.LogInformation("  ModuleMixinInfo: {Status}", _moduleMixinInfoType != null ? "Available" : "Not found");
        _logger.LogInformation("  StrideParsingInfo: {Status}", _strideParsingInfoType != null ? "Available" : "Not found");
    }

    /// <summary>
    /// Check if internal APIs are available.
    /// </summary>
    public bool IsAvailable => _initialized && _available;

    /// <summary>
    /// Get all available types from the Stride.Shaders.Parser assembly for debugging.
    /// </summary>
    public IEnumerable<string> GetAvailableTypes()
    {
        try
        {
            var assembly = typeof(StrideShaderParser).Assembly;
            return assembly.GetTypes()
                .Where(t => t.IsPublic || t.Namespace?.Contains("Mixins") == true)
                .Select(t => t.FullName ?? t.Name)
                .OrderBy(n => n);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enumerate types");
            return Enumerable.Empty<string>();
        }
    }

    /// <summary>
    /// Get methods from ShaderMixinParser for debugging.
    /// </summary>
    public IEnumerable<string> GetShaderMixinParserMethods()
    {
        if (_shaderMixinParserType == null)
            return Enumerable.Empty<string>();

        return _shaderMixinParserType
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
            .Select(m => $"{m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})")
            .OrderBy(s => s);
    }

    /// <summary>
    /// Attempts to get mixin hierarchy information using reflection.
    /// This is a placeholder - actual implementation depends on Stride's internal structure.
    /// </summary>
    public MixinHierarchyInfo? TryGetMixinHierarchy(string shaderSource, string shaderName)
    {
        if (!IsAvailable)
            return null;

        try
        {
            // TODO: Implement actual reflection call to ShaderMixinParser.ParseAndAnalyze
            // This requires understanding the exact method signature and parameters

            // For now, return null to fall back to our own implementation
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get mixin hierarchy via reflection for {Shader}", shaderName);
            return null;
        }
    }
}

/// <summary>
/// Represents mixin hierarchy information extracted from Stride's internal APIs.
/// </summary>
public class MixinHierarchyInfo
{
    public string Name { get; set; } = "";
    public List<string> BaseMixins { get; set; } = new();
    public List<MixinMemberInfo> Members { get; set; } = new();
    public List<MixinHierarchyInfo> ResolvedBases { get; set; } = new();
}

/// <summary>
/// Represents a member from the mixin hierarchy.
/// </summary>
public class MixinMemberInfo
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string DeclaringMixin { get; set; } = "";
    public bool IsOverride { get; set; }
}
