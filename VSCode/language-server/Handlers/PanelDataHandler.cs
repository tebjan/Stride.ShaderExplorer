namespace StrideShaderLanguageServer.Handlers;

#region Request/Response Types for Panel Data

// Inheritance Tree Request/Response
public record InheritanceTreeParams(string Uri);

// Hierarchical shader node with children for tree display
public record ShaderNode(
    string Name,
    string FilePath,
    string Source,
    int Line,
    bool IsLocal,
    List<ShaderNode>? Children = null  // Direct base shaders of this shader
);

public record InheritanceTreeResponse(
    ShaderNode? CurrentShader,
    List<ShaderNode> BaseShaders  // Flat list for backwards compatibility
);

// Shader Members Request/Response
public record ShaderMembersParams(string Uri);

public record MemberInfo(
    string Name,
    string Type,
    string Signature,
    string? Comment,
    int Line,
    string FilePath,
    bool IsLocal,
    string SourceShader,  // Which shader this member comes from
    bool IsStage,  // Whether this member has the 'stage' qualifier
    bool IsEntryPoint  // Whether this method is a shader stage entry point (VSMain, PSMain, etc.)
);

public record MemberGroup(
    string SourceShader,
    string FilePath,
    List<MemberInfo> Members,
    bool IsLocal
);

public record CompositionInfo(
    string Name,
    string Type,
    int Line,
    string FilePath,
    bool IsLocal,
    string SourceShader
);

public record ShaderMembersResponse(
    List<MemberInfo> Streams,
    List<MemberGroup> Variables,
    List<MemberGroup> Methods,
    List<CompositionInfo> Compositions
);

#endregion
