# Stride SDSL Shader Tools

Full IntelliSense for SDSL shaders. Write shaders faster with inheritance-aware completions, real-time diagnostics, and one-click base shader management.

## Features

### Smart Completions

- **Context-aware suggestions** — After `:` in shader declaration, only base shaders appear. After `compose`, only interfaces. After `base.`, only inherited methods.
- **`streams.` completions** — All stream variables from your inheritance chain, sorted by proximity (local first)
- **Inherited members** — Variables and methods from base shaders with origin info
- **HLSL intrinsics** — 100+ functions with signatures (`lerp`, `saturate`, `dot`, `Sample`, etc.)
- **Full type library** — Vectors, matrices, textures, samplers, buffers

### Rich Hover Info

Hover over any identifier to see detailed information:

**Shaders** — Base classes, variable/method count, compositions, file location

**Variables** — Type, qualifiers (`stage`, `stream`, `compose`), inherited or local

**Methods** — Full signature with override chain across inheritance hierarchy

**Swizzles** — Type inference for `.xyz`, `.rg`, etc. (e.g., `float4.xy` → `float2`)

**HLSL types** — Component info for vectors and matrices

**HLSL intrinsics** — Built-in documentation for common functions

### Real-time Diagnostics

Errors and warnings as you type:

| Issue | Severity |
| ----- | -------- |
| `'ColorTarget' is not defined` | Error |
| `Method 'Compute' is marked as override but no base method found` | Error |
| `Base shader 'ShaderBase' not found in workspace` | Warning |
| `Redundant: already inherited via 'MaterialShaderBase'` | Hint (faded) |
| `Cannot convert float4 to float3` | Warning |

### One-Click Base Shader Management

When you reference an undefined variable or method, hover shows which shaders provide it:

```text
Click to add as base shader:
Defined in: ShaderBaseStream
Also via: MaterialShaderBase, ComputeColorTextureBase
```

Click any shader name to instantly add it to your inheritance list.

Similarly, redundant base shaders show a **Remove** link in their hover.

### Sidebar Panels

Four panels in the **Stride Shaders** activity bar:

| Panel | Shows |
| ----- | ----- |
| **Inheritance** | Current shader + all base shaders (click to open) |
| **Streams** | All `stream` variables from the inheritance chain |
| **Variables** | `stage`, `compose`, and regular variables |
| **Methods** | All methods including inherited ones |

Each item shows its source shader. Click to jump to definition.

### External Shader Browsing

- **Workspace shaders** — Open normally (editable)
- **Stride/vvvv shaders** — Open as read-only with visual indicator

Navigate the full Stride shader library without risk of accidental edits.

## Shader Discovery

The extension automatically finds shaders from:

1. **Your workspace** — Any `.sdsl` files in open folders
2. **NuGet packages** — `Stride.*` packages in your global cache
3. **vvvv gamma** — Auto-detects installations in `C:\Program Files\vvvv\`

Add custom paths in settings:

```json
{
  "strideShaderTools.shaderPaths": [
    "C:/MyShaderLibrary",
    "D:/Projects/SharedShaders"
  ]
}
```

## Commands

| Command | Description |
| ------- | ----------- |
| `Stride Shaders: Restart Language Server` | Reload the language server |
| `Stride Shaders: Show Inheritance Tree` | Focus the inheritance panel |
| `Stride Shaders: Refresh Panels` | Update all sidebar panels |

## Settings

| Setting | Default | Description |
| ------- | ------- | ----------- |
| `strideShaderTools.shaderPaths` | `[]` | Additional paths to search for shaders |
| `strideShaderTools.languageServer.path` | `""` | Custom language server path (leave empty for bundled) |
| `strideShaderTools.trace.server` | `off` | LSP trace level (`off`, `messages`, `verbose`) |

## Requirements

- **.NET 8 Runtime** — Automatically installed via the [.NET Install Tool](https://marketplace.visualstudio.com/items?itemName=ms-dotnettools.vscode-dotnet-runtime) extension

## Tips

**Override methods** — Type `override` and see completable methods from base shaders.

**Stream access** — Type `streams.` to see all available stream variables with their source shader.

**Base class calls** — Inside an override, type `base.` to call the parent implementation.

**Quick inheritance** — Can't remember which shader has `ColorTarget`? Just use it — the error hover shows exactly which shaders to inherit from.

## Links

- [Stride Engine](https://stride3d.net/)
- [SDSL Documentation](https://doc.stride3d.net/latest/en/manual/graphics/effects-and-shaders/shading-language/index.html)
- [Report Issues](https://github.com/tebjan/Stride.ShaderExplorer/issues)

---

Made for the Stride community.
