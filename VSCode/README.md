# Stride Shader Tools - VS Code Extension

A VS Code extension for Stride SDSL (Stride Shading Language) with IntelliSense, syntax highlighting, and language server support.

## Features

- **Syntax Highlighting** - Full TextMate grammar for SDSL
- **IntelliSense** - Auto-completion for keywords, types, functions, and shader names
- **Hover Information** - Type info, method signatures, and HLSL intrinsic documentation
- **Diagnostics** - Real-time error detection from Stride's shader parser
- **Inheritance-Aware** - Shows inherited members, base shader info, and override chains
- **Context-Aware Completions** - Smart completions for `base.`, `streams.`, and more

## Requirements

- **.NET 8 SDK** - Required for the language server
- **VS Code 1.95+**

## Quick Start (Development)

### 1. Build Everything

```bash
# Build the Language Server
cd language-server
dotnet build

# Install Extension Dependencies
cd ../vscode-extension
npm install
```

### 2. Debug the Extension

1. Open the root project folder in VS Code
2. Press **F5** (or select "Run SDSL Extension" from Debug panel)
3. A new VS Code window opens with the extension loaded
4. Open `test-workspace/TestShader.sdsl` to test

## Debug Configurations

| Configuration | Description |
|---------------|-------------|
| **Run SDSL Extension** | Full debug with watch mode - auto-rebuilds on changes |
| **Run SDSL Extension (Quick)** | Skip build, use last compiled version |
| **Debug Language Server** | Launch C# server standalone for debugging |
| **SDSL Extension + Server** | Debug both simultaneously |

## Development Workflow

### Watch Mode (Recommended)

```bash
cd vscode-extension
npm run watch
```

Then press **F5** - changes to TypeScript will auto-rebuild.

### Available Scripts

| Script | Description |
|--------|-------------|
| `npm run compile` | Type-check + bundle with esbuild |
| `npm run watch` | Watch mode (parallel tsc + esbuild) |
| `npm run lint` | Check code with Biome |
| `npm run lint:fix` | Auto-fix lint issues |
| `npm run package` | Production build for publishing |

## Project Structure

```
VSCode/
├── vscode-extension/       # TypeScript VS Code extension
│   ├── src/
│   │   └── extension.ts    # Entry point
│   ├── syntaxes/
│   │   └── sdsl.tmLanguage.json
│   ├── dist/               # Bundled output (esbuild)
│   └── package.json
│
├── language-server/        # C# Language Server
│   ├── Program.cs
│   ├── Handlers/           # LSP handlers
│   └── Services/           # Shader parsing & workspace
│
└── test-workspace/         # Sample .sdsl files for testing
    ├── TestShader.sdsl
    └── MaterialShader.sdsl
```

## Configuration

Add to your VS Code `settings.json`:

```json
{
    "strideShaderTools.shaderPaths": [
        "C:/path/to/custom/shaders"
    ],
    "strideShaderTools.languageServer.path": "C:/custom/server/path"
}
```

## Architecture

The extension uses the Language Server Protocol (LSP):

1. **Extension (TypeScript)** - VS Code client that provides UI
2. **Language Server (C#)** - Backend using OmniSharp.Extensions.LanguageServer
3. **Communication** - JSON-RPC over stdio

The language server automatically discovers shaders from:
- Workspace folders
- NuGet cache (`stride.*` packages)
- vvvv gamma installations

## Tooling

- **esbuild** - Fast bundler (10-100x faster than webpack)
- **Biome** - Linter + formatter (replaces ESLint + Prettier)
- **TypeScript 5.7** - Type checking
- **.NET 8** - Language server runtime

## License

MIT
