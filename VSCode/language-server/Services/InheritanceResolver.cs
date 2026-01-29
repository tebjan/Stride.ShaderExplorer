using Microsoft.Extensions.Logging;

namespace StrideShaderLanguageServer.Services;

/// <summary>
/// Resolves shader inheritance chains, providing access to all inherited members.
/// </summary>
public class InheritanceResolver
{
    private readonly ShaderWorkspace _workspace;
    private readonly ILogger<InheritanceResolver> _logger;

    public InheritanceResolver(ShaderWorkspace workspace, ILogger<InheritanceResolver> logger)
    {
        _workspace = workspace;
        _logger = logger;
    }

    /// <summary>
    /// Resolves all base shaders recursively (transitive closure).
    /// Returns base shaders in order from immediate parent to root.
    /// </summary>
    public List<ParsedShader> ResolveInheritanceChain(string shaderName, HashSet<string>? visited = null)
    {
        visited ??= new HashSet<string>();
        if (visited.Contains(shaderName))
        {
            _logger.LogWarning("Circular inheritance detected for shader {ShaderName}", shaderName);
            return new List<ParsedShader>();
        }
        visited.Add(shaderName);

        var result = new List<ParsedShader>();
        var parsed = _workspace.GetParsedShader(shaderName);

        if (parsed?.BaseShaderNames == null)
            return result;

        _logger.LogInformation("Resolving inheritance for {ShaderName}, base shaders: {BaseShaders}",
            shaderName, string.Join(", ", parsed.BaseShaderNames));

        foreach (var baseName in parsed.BaseShaderNames)
        {
            var baseParsed = _workspace.GetParsedShader(baseName);
            if (baseParsed != null)
            {
                _logger.LogInformation("Found base shader {BaseName} with {VarCount} variables, {MethodCount} methods",
                    baseName, baseParsed.Variables.Count, baseParsed.Methods.Count);
                result.Add(baseParsed);
                // Recursively add base shader's bases
                result.AddRange(ResolveInheritanceChain(baseName, visited));
            }
            else
            {
                _logger.LogWarning("Base shader {BaseName} NOT FOUND for {ShaderName}", baseName, shaderName);
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
}
