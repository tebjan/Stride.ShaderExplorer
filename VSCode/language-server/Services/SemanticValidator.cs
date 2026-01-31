using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Stride.Core.Shaders.Ast;
using Stride.Core.Shaders.Ast.Hlsl;
using Stride.Core.Shaders.Ast.Stride;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace StrideShaderLanguageServer.Services;

/// <summary>
/// Performs semantic validation on parsed shaders to detect issues
/// that the syntax parser doesn't catch (like undefined identifiers and type mismatches).
/// </summary>
public class SemanticValidator
{
    private readonly ILogger<SemanticValidator> _logger;
    private readonly ShaderWorkspace _workspace;
    private readonly InheritanceResolver _inheritanceResolver;

    // Built-in HLSL types (for scope building - detailed type info is in HlslTypeSystem)
    private static readonly HashSet<string> BuiltinTypes = new(StringComparer.OrdinalIgnoreCase)
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
        "Color", "Color3", "Color4", "string"
    };

    // Built-in HLSL functions
    private static readonly HashSet<string> BuiltinFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "abs", "acos", "acosh", "asin", "asinh", "atan", "atan2", "atanh",
        "ceil", "clamp", "cos", "cosh", "degrees", "exp", "exp2", "floor",
        "fma", "fmod", "frac", "frexp", "ldexp", "lerp", "log", "log10", "log2",
        "mad", "max", "min", "modf", "pow", "radians", "rcp", "round", "rsqrt",
        "saturate", "sign", "sin", "sincos", "sinh", "smoothstep", "sqrt", "step",
        "tan", "tanh", "trunc",
        "all", "any", "cross", "determinant", "distance", "dot", "dst",
        "faceforward", "length", "lit", "mul", "normalize", "reflect", "refract", "transpose",
        "Sample", "SampleBias", "SampleCmp", "SampleCmpLevelZero", "SampleGrad", "SampleLevel",
        "Load", "GetDimensions", "CalculateLevelOfDetail",
        "Gather", "GatherRed", "GatherGreen", "GatherBlue", "GatherAlpha",
        "ddx", "ddx_coarse", "ddx_fine", "ddy", "ddy_coarse", "ddy_fine", "fwidth",
        "asfloat", "asint", "asuint", "asdouble", "f16tof32", "f32tof16",
        "countbits", "firstbithigh", "firstbitlow", "reversebits",
        "AllMemoryBarrier", "AllMemoryBarrierWithGroupSync",
        "DeviceMemoryBarrier", "DeviceMemoryBarrierWithGroupSync",
        "GroupMemoryBarrier", "GroupMemoryBarrierWithGroupSync",
        "InterlockedAdd", "InterlockedAnd", "InterlockedCompareExchange",
        "InterlockedCompareStore", "InterlockedExchange", "InterlockedMax",
        "InterlockedMin", "InterlockedOr", "InterlockedXor",
        "clip", "isfinite", "isinf", "isnan", "noise"
    };

    // SDSL keywords that look like identifiers
    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "shader", "effect", "mixin", "class", "struct", "namespace", "using",
        "if", "else", "for", "foreach", "while", "do", "switch", "case", "default",
        "break", "continue", "return", "discard",
        "stage", "stream", "streams", "compose", "override", "abstract", "virtual", "clone",
        "static", "const", "extern", "inline", "partial", "internal",
        "cbuffer", "rgroup", "typedef",
        "in", "out", "inout", "uniform", "shared", "groupshared",
        "true", "false", "null", "this", "base"
    };

    public SemanticValidator(
        ILogger<SemanticValidator> logger,
        ShaderWorkspace workspace,
        InheritanceResolver inheritanceResolver)
    {
        _logger = logger;
        _workspace = workspace;
        _inheritanceResolver = inheritanceResolver;
    }

    /// <summary>
    /// Validate a parsed shader and return semantic diagnostics.
    /// </summary>
    public List<Diagnostic> Validate(ParsedShader parsed, string sourceCode, string? filePath = null)
    {
        var diagnostics = new List<Diagnostic>();

        if (parsed.ShaderClass == null)
            return diagnostics;

        try
        {
            // Validate filename matches shader name
            if (!string.IsNullOrEmpty(filePath))
            {
                ValidateFilenameMatchesShaderName(parsed, sourceCode, filePath, diagnostics);
            }

            // Check if this shader itself has duplicates
            ValidateDuplicateShaders(parsed, sourceCode, filePath, diagnostics);

            // Validate base shader references - pass file path for context-aware resolution
            ValidateBaseShaders(parsed, sourceCode, filePath, diagnostics);

            // Validate override methods have base implementations - pass file path for context-aware resolution
            ValidateOverrideMethods(parsed, sourceCode, filePath, diagnostics);

            // Build the scope of known identifiers - pass file path for context-aware resolution
            var scope = BuildScope(parsed, filePath);

            // Walk the AST to find identifier references
            ValidateClass(parsed.ShaderClass, scope, sourceCode, diagnostics);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during semantic validation");
        }

        return diagnostics;
    }

    /// <summary>
    /// Check if this shader has duplicates (multiple files with the same shader name).
    /// </summary>
    private void ValidateDuplicateShaders(ParsedShader parsed, string sourceCode, string? filePath, List<Diagnostic> diagnostics)
    {
        if (string.IsNullOrEmpty(filePath))
            return;

        var shaderName = parsed.Name;
        if (!_workspace.HasDuplicates(shaderName))
            return;

        var allPaths = _workspace.GetAllPathsForShader(shaderName);
        var otherPaths = allPaths.Where(p => !string.Equals(p, filePath, StringComparison.OrdinalIgnoreCase)).ToList();

        if (otherPaths.Count == 0)
            return;

        // Find the shader name position
        var position = FindShaderNamePosition(sourceCode, shaderName);
        if (!position.HasValue)
            return;

        // Build message with other locations
        var otherLocationsDisplay = otherPaths.Select(p => _workspace.GetDisplayPath(p)).ToList();
        var message = $"Shader '{shaderName}' exists in multiple locations. Also found at: {string.Join(", ", otherLocationsDisplay)}";

        diagnostics.Add(new Diagnostic
        {
            Range = new Range(
                position.Value.line,
                position.Value.col,
                position.Value.line,
                position.Value.col + shaderName.Length
            ),
            Severity = DiagnosticSeverity.Warning,
            Source = "sdsl",
            Message = message,
            Code = "duplicate-shader-name"
        });
    }

    /// <summary>
    /// Validate that the filename matches the shader name.
    /// Exception: files ending with " - Copy" or similar Windows copy patterns are only suggested to rename.
    /// </summary>
    private void ValidateFilenameMatchesShaderName(ParsedShader parsed, string sourceCode, string filePath, List<Diagnostic> diagnostics)
    {
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        var shaderName = parsed.Name;

        if (string.Equals(fileName, shaderName, StringComparison.Ordinal))
            return; // All good

        // Check if this is a Windows copy pattern (e.g., "MyShader - Copy", "MyShader (2)")
        var isCopyPattern = fileName.Contains(" - Copy", StringComparison.OrdinalIgnoreCase) ||
                           System.Text.RegularExpressions.Regex.IsMatch(fileName, @"\s*\(\d+\)$");

        // Find the shader name in the source code
        var position = FindShaderNamePosition(sourceCode, shaderName);
        if (!position.HasValue)
            return;

        // Simple error message - quick fix actions will offer the solutions
        var message = $"Filename '{fileName}' doesn't match shader name '{shaderName}'";

        diagnostics.Add(new Diagnostic
        {
            Range = new Range(
                position.Value.line,
                position.Value.col,
                position.Value.line,
                position.Value.col + shaderName.Length
            ),
            Severity = DiagnosticSeverity.Error,
            Source = "sdsl",
            Message = message,
            Code = "shader-filename-mismatch",
            // Store info needed by code action handler
            Data = isCopyPattern ? "copy" : null
        });
    }

    /// <summary>
    /// Find the position of the shader name in the "shader Name" declaration.
    /// </summary>
    private static (int line, int col)? FindShaderNamePosition(string sourceCode, string shaderName)
    {
        var lines = sourceCode.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            // Look for "shader ShaderName" pattern
            var shaderKeywordIndex = line.IndexOf("shader", StringComparison.OrdinalIgnoreCase);
            if (shaderKeywordIndex < 0) continue;

            // Find the shader name after "shader " keyword
            var afterKeyword = shaderKeywordIndex + "shader".Length;
            var remaining = line.Substring(afterKeyword).TrimStart();
            var nameStart = line.Length - remaining.Length;

            // Check if the shader name is there
            if (remaining.StartsWith(shaderName, StringComparison.Ordinal))
            {
                // Verify word boundary after the name (allow < for template params)
                var afterName = shaderName.Length;
                if (afterName >= remaining.Length ||
                    !char.IsLetterOrDigit(remaining[afterName]) ||
                    remaining[afterName] == '<')
                {
                    return (i, nameStart);
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Validate that all base shaders exist and check for redundant inheritance.
    /// </summary>
    private void ValidateBaseShaders(ParsedShader parsed, string sourceCode, string? filePath, List<Diagnostic> diagnostics)
    {
        // First pass: check that all base shaders exist
        // Use BaseShaderReferences to properly handle template arguments
        foreach (var baseRef in parsed.BaseShaderReferences)
        {
            // Use context-aware lookup to find the closest shader among duplicates
            var baseShader = _workspace.GetClosestShaderByName(baseRef.BaseName, filePath);
            if (baseShader == null)
            {
                // Try to find the position in source code using the full name
                var position = FindIdentifierPosition(sourceCode, baseRef.BaseName);
                if (position.HasValue)
                {
                    diagnostics.Add(new Diagnostic
                    {
                        Range = new Range(
                            position.Value.line,
                            position.Value.col,
                            position.Value.line,
                            position.Value.col + baseRef.BaseName.Length
                        ),
                        Severity = DiagnosticSeverity.Warning,
                        Source = "sdsl",
                        Message = $"Base shader '{baseRef.BaseName}' not found in workspace"
                    });
                }
            }
            else
            {
                // Check if base shader has duplicates - show info about which one is used
                if (_workspace.HasDuplicates(baseRef.BaseName))
                {
                    var position = FindBaseShaderPosition(sourceCode, baseRef.BaseName);
                    if (position.HasValue)
                    {
                        var allPaths = _workspace.GetAllPathsForShader(baseRef.BaseName);
                        var usedPath = _workspace.GetDisplayPath(baseShader.FilePath);
                        diagnostics.Add(new Diagnostic
                        {
                            Range = new Range(
                                position.Value.line,
                                position.Value.col,
                                position.Value.line,
                                position.Value.col + baseRef.BaseName.Length
                            ),
                            Severity = DiagnosticSeverity.Information,
                            Source = "sdsl",
                            Message = $"Base shader '{baseRef.BaseName}' exists in {allPaths.Count} locations. Using closest: {usedPath}",
                            Code = "ambiguous-base-shader"
                        });
                    }
                }
            }
        }

        // Second pass: check for redundant base shaders
        // A base shader is redundant if another direct base already inherits from it
        CheckRedundantBaseShaders(parsed, sourceCode, filePath, diagnostics);
    }

    /// <summary>
    /// Check for redundant base shaders that are already transitively inherited via another base.
    /// Example: if shader inherits from A, B and B inherits from A, then A is redundant.
    /// </summary>
    private void CheckRedundantBaseShaders(ParsedShader parsed, string sourceCode, string? filePath, List<Diagnostic> diagnostics)
    {
        var baseNames = parsed.BaseShaderNames;
        if (baseNames.Count < 2)
            return; // Need at least 2 bases for redundancy

        // Build a map of each base shader -> all shaders it transitively inherits
        var transitiveInheritance = new Dictionary<string, HashSet<string>>();

        foreach (var baseName in baseNames)
        {
            // Use context-aware resolution for inheritance chain
            var chain = _inheritanceResolver.ResolveInheritanceChain(baseName, filePath);
            transitiveInheritance[baseName] = new HashSet<string>(
                chain.Select(s => s.Name),
                StringComparer.OrdinalIgnoreCase
            );
        }

        // Check each base shader to see if it's redundantly inherited via another base
        foreach (var baseName in baseNames)
        {
            foreach (var otherBase in baseNames)
            {
                if (string.Equals(baseName, otherBase, StringComparison.OrdinalIgnoreCase))
                    continue;

                // If otherBase transitively inherits from baseName, then baseName is redundant
                if (transitiveInheritance.TryGetValue(otherBase, out var otherChain) &&
                    otherChain.Contains(baseName))
                {
                    var position = FindBaseShaderPosition(sourceCode, baseName);
                    if (position.HasValue)
                    {
                        diagnostics.Add(new Diagnostic
                        {
                            Range = new Range(
                                position.Value.line,
                                position.Value.col,
                                position.Value.line,
                                position.Value.col + baseName.Length
                            ),
                            Severity = DiagnosticSeverity.Hint,
                            Source = "sdsl",
                            Message = $"Redundant: already inherited via '{otherBase}'",
                            Tags = new Container<DiagnosticTag>(DiagnosticTag.Unnecessary),
                            Data = baseName // Store the shader name for quick fix
                        });
                    }
                    break; // Only report once per redundant base
                }
            }
        }
    }

    /// <summary>
    /// Find the position of a base shader name in the inheritance list.
    /// Looks specifically in the "shader Name : Base1, Base2" section.
    /// </summary>
    private static (int line, int col)? FindBaseShaderPosition(string sourceCode, string baseName)
    {
        var lines = sourceCode.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            // Look for shader declaration line
            var colonIndex = line.IndexOf(':');
            if (colonIndex < 0) continue;

            // Check if this looks like a shader declaration
            var beforeColon = line.Substring(0, colonIndex);
            if (!beforeColon.Contains("shader", StringComparison.OrdinalIgnoreCase))
                continue;

            // Search for the base name after the colon
            var afterColon = line.Substring(colonIndex + 1);
            var index = afterColon.IndexOf(baseName, StringComparison.Ordinal);
            if (index < 0) continue;

            // Check word boundaries
            var beforeOk = index == 0 || !char.IsLetterOrDigit(afterColon[index - 1]);
            var afterPos = index + baseName.Length;
            var afterOk = afterPos >= afterColon.Length || !char.IsLetterOrDigit(afterColon[afterPos]);

            if (beforeOk && afterOk)
            {
                return (i, colonIndex + 1 + index);
            }
        }
        return null;
    }

    /// <summary>
    /// Validate that override methods have a corresponding method in a base shader.
    /// </summary>
    private void ValidateOverrideMethods(ParsedShader parsed, string sourceCode, string? filePath, List<Diagnostic> diagnostics)
    {
        foreach (var method in parsed.Methods)
        {
            if (!method.IsOverride)
                continue;

            // Check if any base shader defines this method - use context-aware resolution
            var baseMethodFound = false;
            var inheritanceChain = _inheritanceResolver.ResolveInheritanceChain(parsed.Name, filePath);

            foreach (var baseShader in inheritanceChain)
            {
                if (baseShader.Methods.Any(m => string.Equals(m.Name, method.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    baseMethodFound = true;
                    break;
                }
            }

            if (!baseMethodFound)
            {
                // Find the position of "override" keyword followed by the method name
                var position = FindOverrideMethodPosition(sourceCode, method.Name);
                if (position.HasValue)
                {
                    diagnostics.Add(new Diagnostic
                    {
                        Range = new Range(
                            position.Value.line,
                            position.Value.col,
                            position.Value.line,
                            position.Value.col + method.Name.Length
                        ),
                        Severity = DiagnosticSeverity.Error,
                        Source = "sdsl",
                        Message = $"Method '{method.Name}' is marked as override but no base method found"
                    });
                }
            }
        }
    }

    /// <summary>
    /// Find the position of an override method name in the source code.
    /// Looks for "override ... methodName(" pattern.
    /// </summary>
    private static (int line, int col)? FindOverrideMethodPosition(string sourceCode, string methodName)
    {
        var lines = sourceCode.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            // Look for "override" keyword on this line
            if (!line.Contains("override", StringComparison.OrdinalIgnoreCase))
                continue;

            // Find the method name followed by '('
            var pattern = methodName + "(";
            var index = line.IndexOf(pattern, StringComparison.Ordinal);
            if (index >= 0)
            {
                // Verify it's preceded by word boundary
                var beforeOk = index == 0 || !char.IsLetterOrDigit(line[index - 1]);
                if (beforeOk)
                {
                    return (i, index);
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Find the position of an identifier in the source code.
    /// </summary>
    private static (int line, int col)? FindIdentifierPosition(string sourceCode, string identifier)
    {
        var lines = sourceCode.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            // Look for the identifier as a word boundary match
            var index = 0;
            while ((index = line.IndexOf(identifier, index, StringComparison.Ordinal)) >= 0)
            {
                // Check word boundaries
                var beforeOk = index == 0 || !char.IsLetterOrDigit(line[index - 1]);
                var afterPos = index + identifier.Length;
                var afterOk = afterPos >= line.Length || !char.IsLetterOrDigit(line[afterPos]);

                if (beforeOk && afterOk)
                {
                    return (i, index);
                }
                index++;
            }
        }
        return null;
    }

    private ShaderScope BuildScope(ParsedShader parsed, string? filePath = null)
    {
        var scope = new ShaderScope();

        // Add all builtins
        scope.Types.UnionWith(BuiltinTypes);
        scope.Functions.UnionWith(BuiltinFunctions);

        // Add keywords as special variables
        foreach (var keyword in Keywords)
        {
            scope.AddVariable(keyword, "keyword");
        }

        // Add template parameters as read-only variables
        // Template parameters like "float Intensity" are accessible within the shader
        foreach (var templateParam in parsed.TemplateParameters)
        {
            scope.AddVariable(templateParam.Name, templateParam.TypeName);
        }

        // Add local variables and methods with their types
        foreach (var variable in parsed.Variables)
        {
            scope.AddVariable(variable.Name, variable.TypeName);
        }

        foreach (var method in parsed.Methods)
        {
            scope.Functions.Add(method.Name);
        }

        foreach (var composition in parsed.Compositions)
        {
            scope.AddVariable(composition.Name, composition.TypeName);
        }

        // Add inherited members with their types - use context-aware resolution
        foreach (var (variable, _) in _inheritanceResolver.GetAllVariables(parsed, filePath))
        {
            scope.AddVariable(variable.Name, variable.TypeName);
        }

        foreach (var (method, _) in _inheritanceResolver.GetAllMethods(parsed, filePath))
        {
            scope.Functions.Add(method.Name);
        }

        // Add base shader names as types
        foreach (var baseName in parsed.BaseShaderNames)
        {
            scope.Types.Add(baseName);
        }

        // Add all known shader names as types
        foreach (var name in _workspace.GetAllShaderNames())
        {
            scope.Types.Add(name);
        }

        return scope;
    }

    private void ValidateClass(ClassType classType, ShaderScope scope, string sourceCode, List<Diagnostic> diagnostics)
    {
        foreach (var member in classType.Members)
        {
            if (member is MethodDeclaration method)
            {
                ValidateMethod(method, scope, sourceCode, diagnostics);
            }
            else if (member is Variable variable)
            {
                ValidateVariable(variable, scope, sourceCode, diagnostics);
            }
        }
    }

    private void ValidateMethod(MethodDeclaration method, ShaderScope parentScope, string sourceCode, List<Diagnostic> diagnostics)
    {
        // Create local scope with parameters
        var localScope = new ShaderScope(parentScope);

        foreach (var param in method.Parameters)
        {
            var typeName = param.Type?.Name?.Text ?? "unknown";
            localScope.AddVariable(param.Name.Text, typeName);
        }

        // User-defined methods are MethodDefinition (extends MethodDeclaration) and have Body
        // Built-in methods are just MethodDeclaration (no body)
        if (method is MethodDefinition methodDef && methodDef.Body != null)
        {
            ValidateStatementList(methodDef.Body, localScope, diagnostics);
        }
    }

    private void ValidateStatementList(StatementList body, ShaderScope scope, List<Diagnostic> diagnostics)
    {
        foreach (var stmt in body.Statements)
        {
            ValidateStatement(stmt, scope, diagnostics);
        }
    }

    private void ValidateStatement(Statement statement, ShaderScope scope, List<Diagnostic> diagnostics)
    {
        if (statement == null) return;

        switch (statement)
        {
            case BlockStatement block:
                var blockScope = new ShaderScope(scope);
                foreach (var stmt in block.Statements)
                {
                    ValidateStatement(stmt, blockScope, diagnostics);
                }
                break;

            case DeclarationStatement decl:
                if (decl.Content is Variable v)
                {
                    // Get the declared type
                    var declaredType = v.Type?.Name?.Text ?? "unknown";

                    // Add variable to scope with its type
                    scope.AddVariable(v.Name.Text, declaredType);

                    // Validate initializer and check type compatibility
                    if (v.InitialValue != null)
                    {
                        ValidateExpression(v.InitialValue, scope, diagnostics);

                        // Check type compatibility
                        var initType = InferExpressionType(v.InitialValue, scope);
                        if (initType != null && declaredType != "unknown")
                        {
                            var (typeError, isWarning) = CheckTypeCompatibility(declaredType, initType);
                            if (typeError != null)
                            {
                                var span = v.InitialValue.Span;
                                diagnostics.Add(new Diagnostic
                                {
                                    Range = new Range(
                                        Math.Max(0, span.Location.Line - 1),
                                        Math.Max(0, span.Location.Column - 1),
                                        Math.Max(0, span.Location.Line - 1),
                                        Math.Max(0, span.Location.Column - 1 + 10)
                                    ),
                                    Severity = isWarning ? DiagnosticSeverity.Warning : DiagnosticSeverity.Error,
                                    Source = "sdsl",
                                    Message = isWarning ? $"{typeError}" : $"Type error: {typeError}"
                                });
                            }
                        }
                    }
                }
                break;

            case ExpressionStatement expr:
                ValidateExpression(expr.Expression, scope, diagnostics);
                break;

            case IfStatement ifStmt:
                ValidateExpression(ifStmt.Condition, scope, diagnostics);
                ValidateStatement(ifStmt.Then, scope, diagnostics);
                if (ifStmt.Else != null)
                    ValidateStatement(ifStmt.Else, scope, diagnostics);
                break;

            case ForStatement forStmt:
                var forScope = new ShaderScope(scope);
                if (forStmt.Start != null)
                    ValidateStatement(forStmt.Start, forScope, diagnostics);
                if (forStmt.Condition != null)
                    ValidateExpression(forStmt.Condition, forScope, diagnostics);
                if (forStmt.Next != null)
                    ValidateExpression(forStmt.Next, forScope, diagnostics);
                ValidateStatement(forStmt.Body, forScope, diagnostics);
                break;

            case WhileStatement whileStmt:
                ValidateExpression(whileStmt.Condition, scope, diagnostics);
                ValidateStatement(whileStmt.Statement, scope, diagnostics);
                break;

            case ReturnStatement ret:
                if (ret.Value != null)
                    ValidateExpression(ret.Value, scope, diagnostics);
                break;
        }
    }

    private void ValidateExpression(Expression expr, ShaderScope scope, List<Diagnostic> diagnostics)
    {
        if (expr == null) return;

        switch (expr)
        {
            case VariableReferenceExpression varRef:
                var name = varRef.Name.Text;
                // Check if this is a known variable, function, or type
                if (!scope.IsDefined(name))
                {
                    // Try to get position from the AST span
                    var span = varRef.Span;
                    if (span.Location.Line > 0 || span.Location.Column > 0)
                    {
                        // Search for shaders that define this identifier
                        var message = BuildUndefinedMessage(name);

                        diagnostics.Add(new Diagnostic
                        {
                            Range = new Range(
                                Math.Max(0, span.Location.Line - 1),
                                Math.Max(0, span.Location.Column - 1),
                                Math.Max(0, span.Location.Line - 1),
                                Math.Max(0, span.Location.Column - 1 + name.Length)
                            ),
                            Severity = DiagnosticSeverity.Error,
                            Source = "sdsl",
                            Message = message
                        });
                    }
                }
                break;

            case MemberReferenceExpression memberRef:
                // Validate the target (e.g., 'streams' in 'streams.ColorTarget')
                ValidateExpression(memberRef.Target, scope, diagnostics);

                // For stream access (streams.X), validate the member exists
                if (memberRef.Target is VariableReferenceExpression streamTarget &&
                    HlslTypeSystem.IsStreamType(streamTarget.Name.Text))
                {
                    var memberName = memberRef.Member.Text;
                    if (!scope.IsDefined(memberName))
                    {
                        var span = memberRef.Member.Span;
                        // Fallback to memberRef span if member span is invalid
                        if (span.Location.Line <= 0 && span.Location.Column <= 0)
                            span = memberRef.Span;

                        if (span.Location.Line > 0 || span.Location.Column > 0)
                        {
                            var message = BuildUndefinedMessage(memberName);
                            diagnostics.Add(new Diagnostic
                            {
                                Range = new Range(
                                    Math.Max(0, span.Location.Line - 1),
                                    Math.Max(0, span.Location.Column - 1),
                                    Math.Max(0, span.Location.Line - 1),
                                    Math.Max(0, span.Location.Column - 1 + memberName.Length)
                                ),
                                Severity = DiagnosticSeverity.Error,
                                Source = "sdsl",
                                Message = message
                            });
                        }
                    }
                }
                break;

            case MethodInvocationExpression methodCall:
                ValidateExpression(methodCall.Target, scope, diagnostics);
                foreach (var arg in methodCall.Arguments)
                {
                    ValidateExpression(arg, scope, diagnostics);
                }
                break;

            case BinaryExpression binary:
                ValidateExpression(binary.Left, scope, diagnostics);
                ValidateExpression(binary.Right, scope, diagnostics);
                break;

            case UnaryExpression unary:
                ValidateExpression(unary.Expression, scope, diagnostics);
                break;

            case AssignmentExpression assign:
                ValidateExpression(assign.Target, scope, diagnostics);
                ValidateExpression(assign.Value, scope, diagnostics);

                // Check type compatibility for assignment
                var targetType = InferExpressionType(assign.Target, scope);
                var valueType = InferExpressionType(assign.Value, scope);
                if (targetType != null && valueType != null)
                {
                    var (typeError, isWarningAssign) = CheckTypeCompatibility(targetType, valueType);
                    if (typeError != null)
                    {
                        var span = assign.Value.Span;
                        diagnostics.Add(new Diagnostic
                        {
                            Range = new Range(
                                Math.Max(0, span.Location.Line - 1),
                                Math.Max(0, span.Location.Column - 1),
                                Math.Max(0, span.Location.Line - 1),
                                Math.Max(0, span.Location.Column - 1 + 10)
                            ),
                            Severity = isWarningAssign ? DiagnosticSeverity.Warning : DiagnosticSeverity.Error,
                            Source = "sdsl",
                            Message = isWarningAssign ? $"{typeError}" : $"Type error: {typeError}"
                        });
                    }
                }
                break;

            case ConditionalExpression cond:
                ValidateExpression(cond.Condition, scope, diagnostics);
                ValidateExpression(cond.Left, scope, diagnostics);
                ValidateExpression(cond.Right, scope, diagnostics);
                break;

            case IndexerExpression indexer:
                ValidateExpression(indexer.Target, scope, diagnostics);
                ValidateExpression(indexer.Index, scope, diagnostics);
                break;

            case ParenthesizedExpression paren:
                ValidateExpression(paren.Content, scope, diagnostics);
                break;

            case CastExpression cast:
                ValidateExpression(cast.From, scope, diagnostics);
                break;

            case ArrayInitializerExpression arrayInit:
                foreach (var item in arrayInit.Items)
                {
                    ValidateExpression(item, scope, diagnostics);
                }
                break;
        }
    }

    private void ValidateVariable(Variable variable, ShaderScope scope, string sourceCode, List<Diagnostic> diagnostics)
    {
        // Validate initializer if present
        if (variable.InitialValue != null)
        {
            ValidateExpression(variable.InitialValue, scope, diagnostics);
        }
    }

    /// <summary>
    /// Represents a scope of known identifiers with their types.
    /// </summary>
    private class ShaderScope
    {
        // Variables with their types
        public Dictionary<string, string> VariableTypes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Functions { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Types { get; } = new(StringComparer.OrdinalIgnoreCase);
        private ShaderScope? Parent { get; }

        // For backwards compatibility
        public HashSet<string> Variables => new(VariableTypes.Keys, StringComparer.OrdinalIgnoreCase);

        public ShaderScope(ShaderScope? parent = null)
        {
            Parent = parent;
            if (parent != null)
            {
                // Inherit from parent
                foreach (var kvp in parent.VariableTypes)
                    VariableTypes[kvp.Key] = kvp.Value;
                Functions.UnionWith(parent.Functions);
                Types.UnionWith(parent.Types);
            }
        }

        public void AddVariable(string name, string typeName = "unknown")
        {
            VariableTypes[name] = typeName;
        }

        public string? GetVariableType(string name)
        {
            return VariableTypes.TryGetValue(name, out var type) ? type : null;
        }

        public bool IsDefined(string name)
        {
            return VariableTypes.ContainsKey(name) ||
                   Functions.Contains(name) ||
                   Types.Contains(name);
        }
    }

    /// <summary>
    /// Check if two types are compatible for assignment.
    /// Uses HlslTypeSystem for comprehensive HLSL type checking.
    /// Returns (errorMessage, isWarning) - null errorMessage means compatible.
    /// </summary>
    private static (string? message, bool isWarning) CheckTypeCompatibility(string targetType, string sourceType)
    {
        if (string.IsNullOrEmpty(targetType) || string.IsNullOrEmpty(sourceType))
            return (null, false); // Can't check unknown types

        var result = HlslTypeSystem.CheckConversion(sourceType, targetType);

        if (!result.Allowed)
            return (result.Warning ?? $"Cannot convert {sourceType} to {targetType}", false);

        if (!result.IsImplicit && result.Warning != null)
            return (result.Warning, true); // Warning, not error

        return (null, false);
    }

    /// <summary>
    /// Try to infer the type of an expression.
    /// Returns null if the type cannot be determined.
    /// </summary>
    private string? InferExpressionType(Expression expr, ShaderScope scope)
    {
        if (expr == null) return null;

        switch (expr)
        {
            case LiteralExpression literal:
                return InferLiteralType(literal);

            case VariableReferenceExpression varRef:
                return scope.GetVariableType(varRef.Name.Text);

            case MemberReferenceExpression memberRef:
                var memberName = memberRef.Member.Text;

                // Check if target is a stream type (streams.X, Input.X, etc.)
                // Stream members are stored as shader variables, so look them up in scope
                if (memberRef.Target is VariableReferenceExpression streamRef &&
                    HlslTypeSystem.IsStreamType(streamRef.Name.Text))
                {
                    // Stream member - look up from shader variables in scope
                    var streamMemberType = scope.GetVariableType(memberName);
                    if (streamMemberType != null)
                        return streamMemberType;
                }

                // Check if this is a swizzle operation (e.g., color.xyz)
                var baseType = InferExpressionType(memberRef.Target, scope);

                if (baseType != null && !string.IsNullOrEmpty(memberName))
                {
                    // Try to interpret as swizzle
                    var swizzleResult = HlslTypeSystem.InferSwizzleType(baseType, memberName);
                    if (swizzleResult.ResultType != null)
                        return swizzleResult.ResultType;
                }

                return null;

            case MethodInvocationExpression methodCall:
                // Try to infer from known function return types
                return InferMethodReturnType(methodCall);

            case BinaryExpression binary:
                // For arithmetic ops, result type depends on operands
                var leftType = InferExpressionType(binary.Left, scope);
                var rightType = InferExpressionType(binary.Right, scope);
                return InferBinaryResultType(leftType, rightType, binary.Operator);

            case UnaryExpression unary:
                return InferExpressionType(unary.Expression, scope);

            case CastExpression cast:
                return cast.Target?.Name?.Text;

            case ConditionalExpression cond:
                // Ternary returns type of left/right branch
                return InferExpressionType(cond.Left, scope) ??
                       InferExpressionType(cond.Right, scope);

            case ParenthesizedExpression paren:
                return InferExpressionType(paren.Content, scope);

            case IndexerExpression indexer:
                // Array/vector indexing reduces component count
                var targetType = InferExpressionType(indexer.Target, scope);
                return InferIndexedType(targetType);

            default:
                return null;
        }
    }

    private static string? InferLiteralType(LiteralExpression literal)
    {
        if (literal.Value == null) return null;

        var value = literal.Value;
        if (value is bool) return "bool";
        if (value is int) return "int";
        if (value is uint) return "uint";
        if (value is float) return "float";
        if (value is double) return "double";
        if (value is string) return "string";

        // Check the literal text for type suffixes
        var text = literal.Text ?? "";
        if (text.EndsWith("f", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("."))
            return "float";
        if (text.EndsWith("u", StringComparison.OrdinalIgnoreCase))
            return "uint";

        return "int"; // Default for integer literals
    }

    private static string? InferMethodReturnType(MethodInvocationExpression methodCall)
    {
        // Get method name
        string? methodName = null;
        if (methodCall.Target is VariableReferenceExpression varRef)
            methodName = varRef.Name.Text;
        else if (methodCall.Target is MemberReferenceExpression memberRef)
            methodName = memberRef.Member.Text;

        if (methodName == null) return null;

        // Known HLSL function return types
        return methodName.ToLowerInvariant() switch
        {
            // Scalar functions
            "abs" or "sign" or "floor" or "ceil" or "round" or "trunc" or "frac" => null, // Same as input
            "saturate" or "clamp" or "lerp" or "smoothstep" or "step" => null, // Same as input
            "sin" or "cos" or "tan" or "asin" or "acos" or "atan" or "sinh" or "cosh" or "tanh" => null,
            "exp" or "exp2" or "log" or "log2" or "log10" or "pow" or "sqrt" or "rsqrt" or "rcp" => null,

            // Vector functions returning scalar
            "length" or "distance" => "float",
            "dot" => "float",
            "determinant" => "float",

            // Vector functions returning vector
            "normalize" or "reflect" or "refract" or "faceforward" => null, // Same as input
            "cross" => "float3",

            // Texture sampling - usually returns float4
            "sample" or "samplelevel" or "samplebias" or "samplegrad" => "float4",
            "load" => "float4",

            // Type conversion
            "asfloat" => "float",
            "asint" => "int",
            "asuint" => "uint",

            // Matrix multiply - complex, return null
            "mul" => null,

            _ => null
        };
    }

    private static string? InferBinaryResultType(string? leftType, string? rightType, BinaryOperator op)
    {
        if (leftType == null && rightType == null)
            return null;

        // Convert operator to string for HlslTypeSystem
        var opStr = op switch
        {
            BinaryOperator.Less => "<",
            BinaryOperator.LessEqual => "<=",
            BinaryOperator.Greater => ">",
            BinaryOperator.GreaterEqual => ">=",
            BinaryOperator.Equality => "==",
            BinaryOperator.Inequality => "!=",
            BinaryOperator.LogicalAnd => "&&",
            BinaryOperator.LogicalOr => "||",
            BinaryOperator.Multiply => "*",
            BinaryOperator.Divide => "/",
            BinaryOperator.Plus => "+",
            BinaryOperator.Minus => "-",
            BinaryOperator.Modulo => "%",
            BinaryOperator.BitwiseAnd => "&",
            BinaryOperator.BitwiseOr => "|",
            BinaryOperator.BitwiseXor => "^",
            BinaryOperator.LeftShift => "<<",
            BinaryOperator.RightShift => ">>",
            _ => "+"
        };

        return HlslTypeSystem.InferBinaryResultType(
            leftType ?? "unknown",
            rightType ?? "unknown",
            opStr);
    }

    private static string? InferIndexedType(string? arrayType)
    {
        if (arrayType == null) return null;

        // Indexing a vector returns scalar of same base type
        var info = HlslTypeSystem.GetTypeInfo(arrayType);
        if (info != null && (info.IsVector || info.Rows > 1))
            return info.GetScalarTypeName();

        // For arrays, we'd need array element type info
        return null;
    }

    /// <summary>
    /// Build an error message for an undefined identifier.
    /// The extension handles displaying clickable quick fix links.
    /// </summary>
    private string BuildUndefinedMessage(string name)
    {
        return $"'{name}' is not defined";
    }
}
