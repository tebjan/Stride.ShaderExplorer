# Stride Shader Tools

IntelliSense, debugging, and AI assistance for Stride SDSL shaders.

## Features

### Current (v0.1.0)
- **Syntax Highlighting** - Full SDSL language support including:
  - Shader/mixin declarations with inheritance highlighting
  - HLSL types, vectors, matrices
  - Stride-specific keywords (stage, stream, compose, etc.)
  - Semantics (SV_Position, TEXCOORD, etc.)
  - Built-in functions and intrinsics

### Coming Soon
- **IntelliSense** - Context-aware completions showing inherited members
- **Hover Information** - Type info and documentation on hover
- **Go-to-Definition** - Navigate across shader files
- **Diagnostics** - Real-time error detection
- **Inheritance Tree** - Visual hierarchy panel
- **AI Assistance** - Claude-powered shader suggestions
- **Debugging** - RenderDoc integration with SDSL source mapping

## Requirements

- .NET 8 SDK (for language server features)
- VS Code 1.85.0 or higher

## Extension Settings

- `strideShaderTools.languageServer.path`: Custom path to language server
- `strideShaderTools.shaderPaths`: Additional shader search paths
- `strideShaderTools.trace.server`: Enable LSP communication tracing

## Known Issues

- Language server not yet implemented (syntax highlighting only for now)

## Release Notes

### 0.1.0

- Initial release with syntax highlighting
- SDSL file association and language configuration
- Basic extension infrastructure

---

## Development

```bash
# Install dependencies
npm install

# Compile
npm run compile

# Watch mode
npm run watch

# Package
npx vsce package
```

## Contributing

This extension is part of the [Stride.ShaderExplorer](https://github.com/tebjan/Stride.ShaderExplorer) project.
