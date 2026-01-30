using Microsoft.Extensions.Logging;

namespace StrideShaderLanguageServer.Services;

/// <summary>
/// Resolves shader inheritance chains, providing access to all inherited members.
/// </summary>
public class InheritanceResolver
{
    private readonly ShaderWorkspace _workspace;
    private readonly ILogger<InheritanceResolver> _logger;

    // Cache: shader name -> number of shaders that directly inherit from it
    private Dictionary<string, int>? _childCountCache;
    // Cache: shader name -> resolved inheritance chain (base shaders in order)
    private Dictionary<string, List<ParsedShader>>? _chainCache;
    private readonly object _cacheLock = new();

    public InheritanceResolver(ShaderWorkspace workspace, ILogger<InheritanceResolver> logger)
    {
        _workspace = workspace;
        _logger = logger;

        // Rebuild caches when indexing completes
        _workspace.IndexingComplete += (_, _) => InvalidateCaches();
    }

    /// <summary>
    /// Invalidates all caches (call when shaders are re-indexed).
    /// </summary>
    public void InvalidateCaches()
    {
        lock (_cacheLock)
        {
            _childCountCache = null;
            _chainCache = null;
        }
    }

    /// <summary>
    /// Invalidates the child count cache (call when shaders are re-indexed).
    /// </summary>
    public void InvalidateChildCountCache() => InvalidateCaches();

    /// <summary>
    /// Gets the number of shaders that directly inherit from the given shader.
    /// Higher count = more "popular" as a base shader.
    /// </summary>
    public int GetChildCount(string shaderName)
    {
        EnsureChildCountCache();
        lock (_cacheLock)
        {
            return _childCountCache!.TryGetValue(shaderName, out var count) ? count : 0;
        }
    }

    /// <summary>
    /// Checks if a shader is "popular" (used as direct base by 2+ other shaders).
    /// </summary>
    public bool IsPopularBaseShader(string shaderName) => GetChildCount(shaderName) >= 2;

    private void EnsureChildCountCache()
    {
        lock (_cacheLock)
        {
            if (_childCountCache != null) return;

            _childCountCache = new Dictionary<string, int>();
            var allShaders = _workspace.GetAllShaders();

            foreach (var shader in allShaders)
            {
                var parsed = _workspace.GetParsedShader(shader.Name);
                if (parsed?.BaseShaderReferences == null) continue;

                // Use BaseName (stripped of template arguments) for counting
                foreach (var baseRef in parsed.BaseShaderReferences)
                {
                    var baseName = baseRef.BaseName;
                    if (!_childCountCache.ContainsKey(baseName))
                        _childCountCache[baseName] = 0;
                    _childCountCache[baseName]++;
                }
            }

            _logger.LogDebug("Built child count cache: {Count} base shaders tracked", _childCountCache.Count);
        }
    }

    /// <summary>
    /// Resolves all base shaders recursively (transitive closure).
    /// Returns base shaders in order from immediate parent to root.
    /// Results are cached until the next indexing cycle.
    /// </summary>
    public List<ParsedShader> ResolveInheritanceChain(string shaderName, HashSet<string>? visited = null)
    {
        // For top-level calls, check cache first
        if (visited == null)
        {
            lock (_cacheLock)
            {
                _chainCache ??= new Dictionary<string, List<ParsedShader>>();
                if (_chainCache.TryGetValue(shaderName, out var cached))
                    return cached;
            }

            // Compute the chain
            var chain = ResolveInheritanceChainInternal(shaderName, new HashSet<string>());

            // Cache it (re-check _chainCache in case it was invalidated during computation)
            lock (_cacheLock)
            {
                _chainCache ??= new Dictionary<string, List<ParsedShader>>();
                _chainCache[shaderName] = chain;
            }
            return chain;
        }

        // Recursive call with existing visited set - don't use cache
        return ResolveInheritanceChainInternal(shaderName, visited);
    }

    private List<ParsedShader> ResolveInheritanceChainInternal(string shaderName, HashSet<string> visited)
    {
        if (visited.Contains(shaderName))
        {
            _logger.LogWarning("Circular inheritance detected for shader {ShaderName}", shaderName);
            return new List<ParsedShader>();
        }
        visited.Add(shaderName);

        var result = new List<ParsedShader>();
        var parsed = _workspace.GetParsedShader(shaderName);

        if (parsed?.BaseShaderReferences == null || parsed.BaseShaderReferences.Count == 0)
            return result;

        _logger.LogDebug("Resolving inheritance for {ShaderName}, base shaders: {BaseShaders}",
            shaderName, string.Join(", ", parsed.BaseShaderNames));

        foreach (var baseRef in parsed.BaseShaderReferences)
        {
            // Use BaseName (stripped of template arguments) for lookup
            // e.g., "ColorModulator<1.0f>" -> lookup "ColorModulator"
            var lookupName = baseRef.BaseName;
            var baseParsed = _workspace.GetParsedShader(lookupName);

            if (baseParsed != null)
            {
                _logger.LogDebug("Found base shader {BaseName} (from {FullName}) with {VarCount} variables, {MethodCount} methods{TemplateInfo}",
                    lookupName,
                    baseRef.FullName,
                    baseParsed.Variables.Count,
                    baseParsed.Methods.Count,
                    baseRef.HasTemplateArguments ? $", template args: [{string.Join(", ", baseRef.TemplateArguments)}]" : "");
                result.Add(baseParsed);
                // Recursively add base shader's bases
                result.AddRange(ResolveInheritanceChainInternal(lookupName, visited));
            }
            else
            {
                _logger.LogWarning("Base shader {BaseName} NOT FOUND for {ShaderName} (full ref: {FullName})",
                    lookupName, shaderName, baseRef.FullName);
            }
        }

        return result;
    }

    /// <summary>
    /// Gets all variables including inherited ones.
    /// Returns tuples of (Variable, ShaderName where it's defined).
    /// Local variables come first, then inherited ones in inheritance order.
    /// </summary>
    public IEnumerable<(ShaderVariable Variable, string DefinedIn)> GetAllVariables(ParsedShader shader)
    {
        // Local variables first
        foreach (var v in shader.Variables)
            yield return (v, shader.Name);

        // Then inherited variables
        foreach (var baseShader in ResolveInheritanceChain(shader.Name))
        {
            foreach (var v in baseShader.Variables)
                yield return (v, baseShader.Name);
        }
    }

    /// <summary>
    /// Gets all methods including inherited ones.
    /// Returns tuples of (Method, ShaderName where it's defined).
    /// Local methods come first, then inherited ones in inheritance order.
    /// </summary>
    public IEnumerable<(ShaderMethod Method, string DefinedIn)> GetAllMethods(ParsedShader shader)
    {
        // Local methods first
        foreach (var m in shader.Methods)
            yield return (m, shader.Name);

        // Then inherited methods
        foreach (var baseShader in ResolveInheritanceChain(shader.Name))
        {
            foreach (var m in baseShader.Methods)
                yield return (m, baseShader.Name);
        }
    }

    /// <summary>
    /// Gets all compositions including inherited ones.
    /// </summary>
    public IEnumerable<(ShaderComposition Composition, string DefinedIn)> GetAllCompositions(ParsedShader shader)
    {
        foreach (var c in shader.Compositions)
            yield return (c, shader.Name);

        foreach (var baseShader in ResolveInheritanceChain(shader.Name))
        {
            foreach (var c in baseShader.Compositions)
                yield return (c, baseShader.Name);
        }
    }

    /// <summary>
    /// Finds a variable by name, searching local then inherited.
    /// </summary>
    public (ShaderVariable? Variable, string? DefinedIn) FindVariable(ParsedShader shader, string name)
    {
        return GetAllVariables(shader).FirstOrDefault(x => x.Variable.Name == name);
    }

    /// <summary>
    /// Finds a method by name, searching local then inherited.
    /// Returns the first match (local takes precedence).
    /// </summary>
    public (ShaderMethod? Method, string? DefinedIn) FindMethod(ParsedShader shader, string name)
    {
        return GetAllMethods(shader).FirstOrDefault(x => x.Method.Name == name);
    }

    /// <summary>
    /// Finds ALL methods with a given name across the inheritance chain.
    /// Useful for showing all overrides/implementations of a method.
    /// </summary>
    public IEnumerable<(ShaderMethod Method, string DefinedIn)> FindAllMethodsWithName(ParsedShader shader, string name)
    {
        return GetAllMethods(shader).Where(x => x.Method.Name == name);
    }

    /// <summary>
    /// Finds ALL variables with a given name across the inheritance chain.
    /// </summary>
    public IEnumerable<(ShaderVariable Variable, string DefinedIn)> FindAllVariablesWithName(ParsedShader shader, string name)
    {
        return GetAllVariables(shader).Where(x => x.Variable.Name == name);
    }

    /// <summary>
    /// Search ALL indexed shaders to find which ones define a variable with the given name.
    /// Returns shader names that directly define (not inherit) the variable.
    /// </summary>
    public List<string> FindShadersDefiningVariable(string variableName)
    {
        var result = new List<string>();
        var allShaders = _workspace.GetAllShaders();

        _logger.LogDebug("FindShadersDefiningVariable: searching {Count} indexed shaders for '{Variable}'",
            allShaders.Count, variableName);

        foreach (var shader in allShaders)
        {
            var parsed = _workspace.GetParsedShader(shader.Name);
            if (parsed == null) continue;

            // Check if this shader directly defines (not inherits) the variable
            if (parsed.Variables.Any(v => string.Equals(v.Name, variableName, StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(shader.Name);
            }
        }

        _logger.LogDebug("FindShadersDefiningVariable: found {Count} shaders defining '{Variable}': {Shaders}",
            result.Count, variableName, result.Count > 0 ? string.Join(", ", result) : "NONE");

        return result;
    }

    /// <summary>
    /// Search ALL indexed shaders to find which ones define a method with the given name.
    /// Returns shader names that directly define (not inherit) the method.
    /// </summary>
    public List<string> FindShadersDefiningMethod(string methodName)
    {
        var result = new List<string>();

        foreach (var shader in _workspace.GetAllShaders())
        {
            var parsed = _workspace.GetParsedShader(shader.Name);
            if (parsed == null) continue;

            // Check if this shader directly defines (not inherits) the method
            if (parsed.Methods.Any(m => string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(shader.Name);
            }
        }

        return result;
    }

    /// <summary>
    /// Search ALL indexed shaders to find which ones define a method with matching signature.
    /// Matches by name, return type, and parameter types.
    /// </summary>
    public List<string> FindShadersDefiningMethodWithSignature(ShaderMethod targetMethod)
    {
        var result = new List<string>();

        foreach (var shader in _workspace.GetAllShaders())
        {
            var parsed = _workspace.GetParsedShader(shader.Name);
            if (parsed == null) continue;

            // Check if this shader directly defines a method with matching signature
            if (parsed.Methods.Any(m => MethodSignaturesMatch(m, targetMethod)))
            {
                result.Add(shader.Name);
            }
        }

        return result;
    }

    /// <summary>
    /// Check if two methods have matching signatures (name, return type, parameter types).
    /// </summary>
    private static bool MethodSignaturesMatch(ShaderMethod a, ShaderMethod b)
    {
        // Name must match (case-insensitive)
        if (!string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase))
            return false;

        // Return type must match (case-insensitive)
        if (!string.Equals(a.ReturnType, b.ReturnType, StringComparison.OrdinalIgnoreCase))
            return false;

        // Parameter count must match
        if (a.Parameters.Count != b.Parameters.Count)
            return false;

        // Each parameter type must match (case-insensitive)
        for (int i = 0; i < a.Parameters.Count; i++)
        {
            if (!string.Equals(a.Parameters[i].TypeName, b.Parameters[i].TypeName, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Search ALL indexed shaders to find which ones define a stream variable with the given name.
    /// Streams are typically what's accessed via streams.X syntax.
    /// </summary>
    public List<string> FindShadersDefiningStream(string streamName)
    {
        var result = new List<string>();

        foreach (var shader in _workspace.GetAllShaders())
        {
            var parsed = _workspace.GetParsedShader(shader.Name);
            if (parsed == null) continue;

            // Check if this shader directly defines a stream variable
            if (parsed.Variables.Any(v =>
                string.Equals(v.Name, streamName, StringComparison.OrdinalIgnoreCase) && v.IsStream))
            {
                result.Add(shader.Name);
            }
        }

        return result;
    }

    /// <summary>
    /// Search ALL indexed shaders to find which ones define a stage variable with the given name.
    /// Stage variables are shared across shader stages (like Eye, WorldViewProjection, etc.)
    /// </summary>
    public List<string> FindShadersDefiningStageVariable(string varName)
    {
        var result = new List<string>();

        foreach (var shader in _workspace.GetAllShaders())
        {
            var parsed = _workspace.GetParsedShader(shader.Name);
            if (parsed == null) continue;

            // Check if this shader directly defines a stage variable
            if (parsed.Variables.Any(v =>
                string.Equals(v.Name, varName, StringComparison.OrdinalIgnoreCase) && v.IsStage))
            {
                result.Add(shader.Name);
            }
        }

        return result;
    }

    /// <summary>
    /// Find shaders that provide access to a member (variable or method) either directly or via inheritance.
    /// Returns a smart-filtered list based on:
    /// - Direct definers (always included)
    /// - Workspace/local shaders that inherit the member
    /// - Popular shaders (childCount >= 2) that inherit the member
    /// </summary>
    public ShaderSuggestions FindSmartSuggestions(string memberName, ParsedShader? currentShader)
    {
        var directDefiners = new HashSet<string>();
        var popularInheritors = new HashSet<string>();
        var workspaceInheritors = new HashSet<string>();

        // Find direct definers (variables and methods)
        var varShaders = FindShadersDefiningVariable(memberName);
        var methodShaders = FindShadersDefiningMethod(memberName);
        foreach (var s in varShaders) directDefiners.Add(s);
        foreach (var s in methodShaders) directDefiners.Add(s);

        // Build set of shaders to exclude (already inherited by current shader)
        var alreadyInherited = new HashSet<string>();
        if (currentShader != null)
        {
            alreadyInherited.Add(currentShader.Name);
            foreach (var baseName in currentShader.BaseShaderNames)
                alreadyInherited.Add(baseName);
            foreach (var baseShader in ResolveInheritanceChain(currentShader.Name))
                alreadyInherited.Add(baseShader.Name);
        }

        // Remove already inherited from direct definers
        directDefiners.RemoveWhere(s => alreadyInherited.Contains(s));

        // Now find shaders that inherit from the direct definers
        foreach (var definer in directDefiners.ToList())
        {
            foreach (var shader in _workspace.GetAllShaders())
            {
                if (alreadyInherited.Contains(shader.Name)) continue;
                if (directDefiners.Contains(shader.Name)) continue;

                // Check if this shader inherits from the definer
                var inheritanceChain = ResolveInheritanceChain(shader.Name);
                if (inheritanceChain.Any(b => b.Name == definer))
                {
                    // This shader provides access to the member via inheritance
                    if (shader.Source == ShaderSource.Workspace)
                    {
                        workspaceInheritors.Add(shader.Name);
                    }
                    else if (IsPopularBaseShader(shader.Name))
                    {
                        popularInheritors.Add(shader.Name);
                    }
                }
            }
        }

        _logger.LogDebug("FindSmartSuggestions for '{Member}': direct={Direct}, popular={Popular}, workspace={Workspace}",
            memberName,
            string.Join(", ", directDefiners),
            string.Join(", ", popularInheritors),
            string.Join(", ", workspaceInheritors));

        return new ShaderSuggestions
        {
            DirectDefiners = directDefiners.ToList(),
            PopularInheritors = popularInheritors.ToList(),
            WorkspaceInheritors = workspaceInheritors.ToList()
        };
    }
}

/// <summary>
/// Result of smart shader suggestion search.
/// </summary>
public class ShaderSuggestions
{
    /// <summary>Shaders that directly define the member.</summary>
    public List<string> DirectDefiners { get; set; } = new();

    /// <summary>Popular shaders (used by 2+ others) that provide access via inheritance.</summary>
    public List<string> PopularInheritors { get; set; } = new();

    /// <summary>Workspace/local shaders that provide access via inheritance.</summary>
    public List<string> WorkspaceInheritors { get; set; } = new();

    /// <summary>Check if any suggestions are available.</summary>
    public bool HasSuggestions => DirectDefiners.Count > 0 || PopularInheritors.Count > 0 || WorkspaceInheritors.Count > 0;

    /// <summary>Get all suggestions as a flat list (direct first, then popular, then workspace).</summary>
    public List<string> GetAllSuggestions(int maxCount = 5)
    {
        var result = new List<string>();
        result.AddRange(DirectDefiners);
        result.AddRange(PopularInheritors.Where(s => !result.Contains(s)));
        result.AddRange(WorkspaceInheritors.Where(s => !result.Contains(s)));
        return result.Take(maxCount).ToList();
    }
}
