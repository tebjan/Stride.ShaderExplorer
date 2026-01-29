using Microsoft.Extensions.Logging;
using Stride.Core.Shaders.Ast;
using Stride.Core.Shaders.Ast.Hlsl;
using Stride.Core.Shaders.Ast.Stride;
using Stride.Shaders.Parser;
using ShaderMacro = Stride.Core.Shaders.Parser.ShaderMacro;
using SourceSpan = Stride.Core.Shaders.Ast.SourceSpan;

namespace StrideShaderLanguageServer.Services;

// Extension method for Shader AST
internal static class ShaderExtensions
{
    public static ClassType? GetFirstClassDecl(this Shader shader)
    {
        var result = shader.Declarations.OfType<ClassType>().FirstOrDefault();
        if (result == null)
        {
            var nameSpace = shader.Declarations.OfType<NamespaceBlock>().FirstOrDefault();
            if (nameSpace != null)
            {
                result = nameSpace.Body.OfType<ClassType>().FirstOrDefault();
            }
        }
        return result;
    }
}

/// <summary>
/// Parses SDSL shader files using Stride's shader parser.
/// </summary>
public class ShaderParser
{
    private readonly ILogger<ShaderParser> _logger;
    private readonly Dictionary<string, ParsedShader> _cache = new();
    private readonly object _cacheLock = new();

    private static readonly ShaderMacro[] Macros = new[]
    {
        new ShaderMacro("class", "shader")
    };

    public ShaderParser(ILogger<ShaderParser> logger)
    {
        _logger = logger;
    }

    public ParsedShader? TryParse(string shaderName, string sourceCode)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(shaderName, out var cached))
            {
                return cached;
            }

            try
            {
                var inputFileName = shaderName + ".sdsl";
                var parsingResult = StrideShaderParser.TryPreProcessAndParse(sourceCode, inputFileName, Macros);

                if (parsingResult.HasErrors)
                {
                    _logger.LogWarning("Failed to parse shader {ShaderName}: {Errors}",
                        shaderName,
                        string.Join(", ", parsingResult.Messages.Select(m => m.Text)));
                    return null;
                }

                var shaderClass = parsingResult.Shader?.GetFirstClassDecl();
                if (shaderClass == null)
                {
                    _logger.LogWarning("No shader class found in {ShaderName}", shaderName);
                    return null;
                }

                var parsed = new ParsedShader(shaderName, parsingResult.Shader!, shaderClass);
                _cache[shaderName] = parsed;

                _logger.LogDebug("Successfully parsed shader {ShaderName}", shaderName);
                return parsed;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception while parsing shader {ShaderName}", shaderName);
                return null;
            }
        }
    }

    public void InvalidateCache(string? shaderName = null)
    {
        lock (_cacheLock)
        {
            if (shaderName != null)
            {
                _cache.Remove(shaderName);
            }
            else
            {
                _cache.Clear();
            }
        }
    }

    public IReadOnlyCollection<string> GetCachedShaderNames()
    {
        lock (_cacheLock)
        {
            return _cache.Keys.ToList();
        }
    }
}

/// <summary>
/// Represents a parsed SDSL shader with its AST and extracted information.
/// </summary>
public class ParsedShader
{
    public string Name { get; }
    public Shader Shader { get; }
    public ClassType ShaderClass { get; }

    public IReadOnlyList<string> BaseShaderNames { get; }
    public IReadOnlyList<ShaderVariable> Variables { get; }
    public IReadOnlyList<ShaderMethod> Methods { get; }
    public IReadOnlyList<ShaderComposition> Compositions { get; }

    public ParsedShader(string name, Shader shader, ClassType shaderClass)
    {
        Name = name;
        Shader = shader;
        ShaderClass = shaderClass;

        // Extract base shader names
        BaseShaderNames = shaderClass.BaseClasses?
            .Select(bc => bc.Name.Text)
            .ToList() ?? new List<string>();

        // Extract variables
        var variables = shaderClass.Members.OfType<Variable>().ToList();
        Variables = variables.Select(v => new ShaderVariable(v)).ToList();

        // Extract methods
        Methods = shaderClass.Members
            .OfType<MethodDeclaration>()
            .Select(m => new ShaderMethod(m))
            .ToList();

        // Extract compositions (variables with 'compose' qualifier)
        Compositions = variables
            .Where(v => v.Qualifiers.Contains(StrideStorageQualifier.Compose))
            .Select(v => new ShaderComposition(v))
            .ToList();
    }
}

public class ShaderVariable
{
    public string Name { get; }
    public string TypeName { get; }
    public bool IsStage { get; }
    public bool IsStream { get; }
    public bool IsCompose { get; }
    public SourceSpan Location { get; }

    public ShaderVariable(Variable variable)
    {
        Name = variable.Name.Text;
        TypeName = variable.Type?.Name?.Text ?? "unknown";
        IsStage = variable.Qualifiers.Contains(StrideStorageQualifier.Stage);
        IsStream = variable.Qualifiers.Contains(StrideStorageQualifier.Stream);
        IsCompose = variable.Qualifiers.Contains(StrideStorageQualifier.Compose);
        Location = variable.Span;
    }
}

public class ShaderMethod
{
    public string Name { get; }
    public string ReturnType { get; }
    public IReadOnlyList<ShaderParameter> Parameters { get; }
    public bool IsOverride { get; }
    public bool IsAbstract { get; }
    public bool IsStage { get; }
    public SourceSpan Location { get; }

    public ShaderMethod(MethodDeclaration method)
    {
        Name = method.Name.Text;
        ReturnType = method.ReturnType?.Name?.Text ?? "void";
        Parameters = method.Parameters
            .Select(p => new ShaderParameter(p.Name.Text, p.Type?.Name?.Text ?? "unknown"))
            .ToList();
        // Check for override/abstract in qualifiers text representation
        var qualText = method.Qualifiers.ToString();
        IsOverride = qualText.Contains("override");
        IsAbstract = qualText.Contains("abstract");
        IsStage = method.Qualifiers.Contains(StrideStorageQualifier.Stage);
        Location = method.Span;
    }
}

public class ShaderParameter
{
    public string Name { get; }
    public string TypeName { get; }

    public ShaderParameter(string name, string typeName)
    {
        Name = name;
        TypeName = typeName;
    }
}

public class ShaderComposition
{
    public string Name { get; }
    public string TypeName { get; }
    public SourceSpan Location { get; }

    public ShaderComposition(Variable variable)
    {
        Name = variable.Name.Text;
        TypeName = variable.Type?.Name?.Text ?? "unknown";
        Location = variable.Span;
    }
}
