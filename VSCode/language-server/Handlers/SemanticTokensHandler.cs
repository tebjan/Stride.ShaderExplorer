using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using StrideShaderLanguageServer.Services;

namespace StrideShaderLanguageServer.Handlers;

public class SemanticTokensHandler : SemanticTokensHandlerBase
{
    private readonly ILogger<SemanticTokensHandler> _logger;
    private readonly ShaderWorkspace _workspace;
    private readonly TextDocumentSyncHandler _syncHandler;

    // Token type indices - must match the legend order in CreateRegistrationOptions
    private static class TokenTypes
    {
        public const int Type = 0;       // Generic type references
        public const int Struct = 1;     // struct definitions and references
        public const int Class = 2;      // shader definitions
        public const int Interface = 3;  // interface types (IComputeColor, etc.)
    }

    // Token modifier flags
    private static class TokenModifiers
    {
        public const int Declaration = 1 << 0;
        public const int Definition = 1 << 1;
    }

    public SemanticTokensHandler(
        ILogger<SemanticTokensHandler> logger,
        ShaderWorkspace workspace,
        TextDocumentSyncHandler syncHandler)
    {
        _logger = logger;
        _workspace = workspace;
        _syncHandler = syncHandler;
    }

    protected override Task Tokenize(
        SemanticTokensBuilder builder,
        ITextDocumentIdentifierParams identifier,
        CancellationToken cancellationToken)
    {
        var content = _syncHandler.GetDocumentContent(identifier.TextDocument.Uri);
        if (string.IsNullOrEmpty(content))
        {
            return Task.CompletedTask;
        }

        // Get the file path for context-aware type resolution
        var contextPath = identifier.TextDocument.Uri.GetFileSystemPath();

        var lines = content.Split('\n');

        // First pass: collect locally defined structs
        var localStructs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            var structMatch = Regex.Match(line, @"^\s*struct\s+(\w+)");
            if (structMatch.Success)
            {
                localStructs.Add(structMatch.Groups[1].Value);
            }
        }

        // Second pass: emit tokens
        for (int lineNum = 0; lineNum < lines.Length; lineNum++)
        {
            var line = lines[lineNum].TrimEnd('\r');
            ProcessLine(builder, line, lineNum, localStructs, contextPath);
        }

        return Task.CompletedTask;
    }

    private void ProcessLine(SemanticTokensBuilder builder, string line, int lineNum, HashSet<string> localStructs, string? contextPath)
    {
        // 1. Shader declaration: "shader MyShader : Base1, Base2"
        var shaderDeclMatch = Regex.Match(line, @"^\s*shader\s+(\w+)");
        if (shaderDeclMatch.Success)
        {
            var shaderName = shaderDeclMatch.Groups[1];
            PushToken(builder, lineNum, shaderName.Index, shaderName.Length,
                TokenTypes.Class, TokenModifiers.Declaration | TokenModifiers.Definition);

            // Base shaders after ":"
            var colonIdx = line.IndexOf(':');
            if (colonIdx > 0)
            {
                ProcessBaseShaderList(builder, line, lineNum, colonIdx + 1);
            }
            return;
        }

        // 2. Struct declaration: "struct Particle"
        var structDeclMatch = Regex.Match(line, @"^\s*struct\s+(\w+)");
        if (structDeclMatch.Success)
        {
            var structName = structDeclMatch.Groups[1];
            PushToken(builder, lineNum, structName.Index, structName.Length,
                TokenTypes.Struct, TokenModifiers.Declaration | TokenModifiers.Definition);
            return;
        }

        // 3. Compose declarations: "compose ComputeColor albedoMap;"
        var composeMatch = Regex.Match(line, @"\bcompose\s+(\w+)\s+\w+");
        if (composeMatch.Success)
        {
            var typeName = composeMatch.Groups[1];
            var tokenType = IsInterfaceType(typeName.Value) ? TokenTypes.Interface : TokenTypes.Type;
            PushToken(builder, lineNum, typeName.Index, typeName.Length, tokenType, 0);
        }

        // 4. Method declarations: "ReturnType MethodName(ParamType param)"
        var methodMatch = Regex.Match(line, @"^\s*(?:override\s+|stage\s+|abstract\s+)*(\w+)\s+(\w+)\s*\(([^)]*)\)");
        if (methodMatch.Success)
        {
            // Return type
            var returnType = methodMatch.Groups[1];
            if (IsCustomType(returnType.Value, localStructs, contextPath))
            {
                var tokenType = GetTypeTokenType(returnType.Value, localStructs, contextPath);
                PushToken(builder, lineNum, returnType.Index, returnType.Length, tokenType, 0);
            }

            // Parameter types
            var paramsStr = methodMatch.Groups[3].Value;
            var paramsStart = methodMatch.Groups[3].Index;
            ProcessParameterTypes(builder, paramsStr, lineNum, paramsStart, localStructs, contextPath);
        }

        // 5. Variable declarations with custom types: "Particle p = ..."
        // Skip if this is a shader/struct/compose declaration
        if (!line.TrimStart().StartsWith("shader") &&
            !line.TrimStart().StartsWith("struct") &&
            !line.Contains("compose "))
        {
            // Look for type declarations: Type varName = ... or Type varName;
            foreach (Match match in Regex.Matches(line, @"\b(\w+)\s+(\w+)\s*[=;,\)]"))
            {
                var typeName = match.Groups[1].Value;
                if (IsCustomType(typeName, localStructs, contextPath) && !IsKeyword(typeName))
                {
                    var tokenType = GetTypeTokenType(typeName, localStructs, contextPath);
                    PushToken(builder, lineNum, match.Groups[1].Index, typeName.Length, tokenType, 0);
                }
            }
        }

        // 6. Generic type arguments: RWStructuredBuffer<Particle>
        foreach (Match match in Regex.Matches(line, @"<(\w+)>"))
        {
            var innerType = match.Groups[1].Value;
            if (IsCustomType(innerType, localStructs, contextPath))
            {
                var tokenType = GetTypeTokenType(innerType, localStructs, contextPath);
                PushToken(builder, lineNum, match.Groups[1].Index, innerType.Length, tokenType, 0);
            }
        }
    }

    private void ProcessBaseShaderList(SemanticTokensBuilder builder, string line, int lineNum, int startIdx)
    {
        // Parse base shader names separated by commas
        var remainder = line.Substring(startIdx);
        var idx = startIdx;

        // Match shader names, handling generics like ShaderBase<T>
        foreach (Match match in Regex.Matches(remainder, @"(\w+)(?:<[^>]+>)?"))
        {
            var shaderName = match.Groups[1].Value;
            var absoluteIdx = idx + match.Groups[1].Index;

            // Check if it's a known shader or interface
            var tokenType = IsInterfaceType(shaderName) ? TokenTypes.Interface : TokenTypes.Type;
            PushToken(builder, lineNum, absoluteIdx, shaderName.Length, tokenType, 0);
        }
    }

    private void ProcessParameterTypes(SemanticTokensBuilder builder, string paramsStr, int lineNum, int paramsStart, HashSet<string> localStructs, string? contextPath)
    {
        // Match "Type paramName" patterns
        foreach (Match match in Regex.Matches(paramsStr, @"(\w+)\s+\w+"))
        {
            var paramType = match.Groups[1].Value;
            if (IsCustomType(paramType, localStructs, contextPath))
            {
                var tokenType = GetTypeTokenType(paramType, localStructs, contextPath);
                var absoluteIdx = paramsStart + match.Groups[1].Index;
                PushToken(builder, lineNum, absoluteIdx, paramType.Length, tokenType, 0);
            }
        }
    }

    private void PushToken(SemanticTokensBuilder builder, int line, int startChar, int length, int tokenType, int modifiers)
    {
        try
        {
            builder.Push(line, startChar, length, tokenType, modifiers);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to push semantic token at {Line}:{Char}", line, startChar);
        }
    }

    private bool IsCustomType(string name, HashSet<string> localStructs, string? contextPath)
    {
        // Skip HLSL built-in types
        if (IsHlslBuiltinType(name))
            return false;

        // Skip keywords
        if (IsKeyword(name))
            return false;

        // Check if it's a local struct
        if (localStructs.Contains(name))
            return true;

        // Check if it's a known shader (context-aware for duplicates)
        if (_workspace.GetClosestShaderByName(name, contextPath) != null)
            return true;

        // Check if it's a struct from any shader in the workspace (context-aware)
        if (_workspace.IsStructType(name, contextPath))
            return true;

        // Check if it starts with I (interface pattern)
        if (IsInterfaceType(name))
            return true;

        return false;
    }

    private static bool IsHlslBuiltinType(string name)
    {
        // Common HLSL types that shouldn't be highlighted as custom types
        var builtinTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "void", "bool", "int", "uint", "dword", "half", "float", "double",
            "bool2", "bool3", "bool4",
            "int2", "int3", "int4",
            "uint2", "uint3", "uint4",
            "half2", "half3", "half4",
            "float2", "float3", "float4",
            "double2", "double3", "double4",
            "float2x2", "float3x3", "float4x4",
            "float2x3", "float2x4", "float3x2", "float3x4", "float4x2", "float4x3",
            "matrix", "vector",
            "Texture2D", "Texture3D", "TextureCube", "Texture2DArray",
            "SamplerState", "SamplerComparisonState",
            "RWTexture2D", "RWTexture3D",
            "StructuredBuffer", "RWStructuredBuffer", "ByteAddressBuffer", "RWByteAddressBuffer"
        };
        return builtinTypes.Contains(name);
    }

    private static bool IsKeyword(string name)
    {
        var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "return", "if", "else", "for", "while", "do", "switch", "case", "break",
            "continue", "default", "const", "static", "override", "stage", "stream",
            "compose", "shader", "struct", "class", "mixin",
            "in", "out", "inout", "uniform", "varying", "discard", "true", "false"
        };
        return keywords.Contains(name);
    }

    private static bool IsInterfaceType(string name)
    {
        // Interface naming convention: starts with I followed by uppercase
        return name.Length > 1 && name[0] == 'I' && char.IsUpper(name[1]);
    }

    private int GetTypeTokenType(string typeName, HashSet<string> localStructs, string? contextPath)
    {
        if (IsInterfaceType(typeName))
            return TokenTypes.Interface;

        // Check if it's a struct (local or from workspace, context-aware)
        if (localStructs.Contains(typeName) || _workspace.IsStructType(typeName, contextPath))
            return TokenTypes.Struct;

        // Default to Type (for shaders and other custom types)
        return TokenTypes.Type;
    }

    protected override Task<SemanticTokensDocument> GetSemanticTokensDocument(
        ITextDocumentIdentifierParams @params,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new SemanticTokensDocument(RegistrationOptions.Legend));
    }

    protected override SemanticTokensRegistrationOptions CreateRegistrationOptions(
        SemanticTokensCapability capability,
        ClientCapabilities clientCapabilities)
    {
        return new SemanticTokensRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForPattern("**/*.sdsl"),
            Legend = new SemanticTokensLegend
            {
                TokenTypes = new Container<SemanticTokenType>(
                    SemanticTokenType.Type,       // 0 - generic type references
                    SemanticTokenType.Struct,     // 1 - struct definitions/references
                    SemanticTokenType.Class,      // 2 - shader definitions
                    SemanticTokenType.Interface   // 3 - interface types
                ),
                TokenModifiers = new Container<SemanticTokenModifier>(
                    SemanticTokenModifier.Declaration,  // 0
                    SemanticTokenModifier.Definition    // 1
                )
            },
            Full = new SemanticTokensCapabilityRequestFull
            {
                Delta = false
            },
            Range = true
        };
    }
}
