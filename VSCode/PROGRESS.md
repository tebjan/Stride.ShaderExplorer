# Stride SDSL Shader IDE - Progress Tracker

*Last updated: January 2026*

---

## COMPLETED FEATURES

### Phase 1: Extension Scaffold
- [x] VS Code extension project with TypeScript
- [x] TextMate grammar for SDSL syntax highlighting
- [x] `.sdsl` file association with custom icons
- [x] C# Language Server with OmniSharp.Extensions.LanguageServer
- [x] stdio communication between extension and server
- [x] .NET 8 runtime auto-acquisition via `ms-dotnettools.vscode-dotnet-runtime`

### Phase 2: Core Language Server
- [x] **ShaderParser** - Parses SDSL using `Stride.Shaders.Parser` NuGet
- [x] **ParsedShader** - Extracts AST info (inheritance, members, compositions)
- [x] **TextDocumentSyncHandler** - Tracks open files, debounced diagnostics
- [x] **CompletionHandler** - Keywords, HLSL intrinsics, shader names, inherited members
- [x] **HoverHandler** - Type info, method signatures, inheritance chain display
- [x] **DiagnosticsPublisher** - Parse errors as VS Code problems with friendly messages
- [x] **SignatureHelpHandler** - Function parameter hints

### Phase 3: Inheritance Resolution
- [x] **ShaderWorkspace** - Indexes all shaders (NuGet, vvvv, workspace)
- [x] **InheritanceResolver** - Full hierarchy traversal
  - `ResolveInheritanceChain()` - Get all base shaders
  - `GetAllVariables()` - Variables from entire hierarchy
  - `GetAllMethods()` - Methods with override tracking
  - `GetAllCompositions()` - Composition dependencies
  - `FindVariable()` / `FindMethod()` - Locate definitions
- [x] **DefinitionHandler** - F12/Ctrl+Click go-to-definition
  - Jump to base shader declarations
  - Jump to method definitions in inheritance chain
  - Jump to variable definitions
  - Jump to struct definitions

### Phase 4: Semantic Intelligence
- [x] **SemanticTokensHandler** - Inheritance-aware syntax coloring
  - Different colors for: shaders, types, methods, variables, streams
  - Override methods highlighted differently
- [x] **SemanticValidator** - Smart diagnostics
  - Unknown base shader detection with "Did you mean?" suggestions
  - Orphaned override method detection (no base to override)
  - Redundant base shader detection
- [x] **CodeActionHandler** - Quick fixes
  - Add missing base shader
  - Remove redundant base shader

### Phase 5: Unified Sidebar Panel
- [x] **UnifiedTreeProvider** - Single consolidated TreeView replacing 4 separate panels
  - Current shader at root
  - Expandable categories: Inheritance, Streams, Variables, Methods
  - Member counts in category headers
  - `stage` keyword displayed in labels
  - Entry point methods (VSMain, PSMain, etc.) with special icons
  - Click-to-navigate to definitions
  - Local vs inherited member distinction (● local, ○ inherited)
- [x] Debounced soft refresh while typing (preserves focus)

### Phase 6: Quick Fix UI
- [x] Clickable **"Add: ShaderName"** links in hover tooltips
- [x] Smart shader suggestions:
  - DirectDefiners (shaders that define the variable/method)
  - PopularInheritors (commonly used base shaders)
  - WorkspaceInheritors (shaders in user's project)
- [x] Clickable **"Remove: ShaderName"** for redundant bases
- [x] Hover on `base.Method()` shows which base implementation is called

### Phase 7: Path Discovery & Display
- [x] **NuGet packages** auto-discovery (`%NUGET_PACKAGES%` or `~/.nuget/packages`)
- [x] **vvvv gamma** auto-discovery with smart version selection:
  - Parses version from directory names (e.g., `vvvv_gamma_7.1-0144-...`)
  - Filters out special versions (-hdr, -beta, -alpha, -rc)
  - Selects highest version (preview > stable for same major.minor)
  - Clean display format: `vvvv@7.1-144/VL.Stride/...`
- [x] **Workspace folders** added automatically
- [x] **Display paths** show clean format instead of full paths:
  - `Stride.Rendering@4.2.0/Materials/...`
  - `vvvv@7.1-144/VL.Stride.Runtime/...`

### Phase 8: External Shader Handling
- [x] **ExternalShaderProvider** - Virtual filesystem for read-only shaders
- [x] Stride/vvvv shaders open as read-only (prevents accidental edits)
- [x] Workspace shaders open as editable
- [x] Status bar indicator for external shaders

### Phase 9: Robust Shutdown
- [x] **ShutdownHandler** - Proper LSP shutdown/exit handling
- [x] **Cancellation** for background indexing tasks
- [x] **Dispose pattern** for TextDocumentSyncHandler
- [x] Cleanup of debounced diagnostic tasks
- [x] No more hanging processes after extension stops

### Phase 10: Configurable Settings
- [x] `strideShaderTools.diagnostics.delay` - Debounce delay (100ms - 10000ms, default 2000ms)
- [x] `strideShaderTools.shaderPaths` - Additional shader search paths
- [x] `strideShaderTools.languageServer.path` - Custom server path
- [x] `strideShaderTools.trace.server` - LSP trace level

---

## NOTABLE IMPLEMENTATION DETAILS

### Hover Features
- **Variables**: Shows type, source shader, whether it's a stream/stage
- **Methods**: Shows signature, override chain with all base implementations
- **base.Method()**: Shows exactly which base implementation will be called
- **Streams**: Shows stream type and source shader
- **HLSL Intrinsics**: Built-in documentation for lerp, saturate, dot, normalize, etc.

### Completion Features
- **After `shader X : `**: Suggests available base shaders
- **After `compose`**: Suggests compatible composition types
- **In method body**: Inherited variables, methods, streams
- **After `.`**: Type-aware member suggestions

### Diagnostic Features
- **Parse errors**: Friendly messages with position adjustment to actual error location
- **Unknown shader**: "Did you mean 'X'?" suggestions
- **Orphaned override**: Method has `override` but no base method exists
- **Redundant base**: Base shader is already inherited via another path

### TreeView Features
- **Entry points** (VSMain, PSMain, CSMain, etc.) get green play icon
- **Stage members** show `stage` prefix in label
- **Counts** in category headers (e.g., "Methods (12)")
- **Descriptions** show source shader name

---

## TODO - NEXT PHASES

### Phase: Robust Type System
- [ ] Complete HLSL type system in `HlslTypeSystem.cs`
- [ ] Scalar promotion hierarchy: `Double > Float > Half > UInt > Int > Bool`
- [ ] Vector truncation warnings (float4 -> float3)
- [ ] Vector extension errors (float3 -> float4 without explicit)
- [ ] Swizzle validation (can't mix `.xyzw` with `.rgba`)
- [ ] Matrix type checking
- [ ] Integration with SemanticValidator for type mismatch diagnostics

### Phase: Enhanced Completions
- [ ] Completion details with documentation
- [ ] Snippet completions for common patterns
- [ ] Auto-import suggestions for missing base shaders
- [ ] Parameter hints in function calls

### Phase: Find References
- [ ] **ReferencesHandler** - Shift+F12 find all references
- [ ] Find usages of variables across inheritance chain
- [ ] Find overrides of methods
- [ ] Find usages of compositions

### Phase: Rename Symbol
- [ ] Rename variable/method across current shader
- [ ] Rename with impact analysis (show affected files)

### Phase: README & Documentation
- [ ] Document F12/Ctrl+Click navigation
- [ ] Document all settings
- [ ] Add feature screenshots
- [ ] Marketplace description

---

## FUTURE VISION

### RenderDoc Debugger Integration
- [ ] Research Stride's EffectCompiler HLSL generation
- [ ] Source map format: `{ hlslLine -> (sdslFile, sdslLine) }`
- [ ] Hook into Stride compilation to emit source maps
- [ ] VS Code debug configuration for RenderDoc
- [ ] Debug Adapter Protocol (DAP) server
- [ ] Breakpoint translation (SDSL <-> HLSL)
- [ ] Variable inspection panel
- [ ] Vector watch visualization (3D arrows for normals, etc.)

### AI Integration
- [ ] **ContextBuilder** - Extract full shader context for AI prompts
- [ ] Claude API integration
- [ ] Inline completion provider (ghost text suggestions)
- [ ] AI chat webview panel
- [ ] "What can I add?" suggestions based on shader type

### Advanced Tooling
- [ ] Live preview integration (if feasible with Stride)
- [ ] Performance hints (texture samples, ALU ops, branches)
- [ ] Snippet library (fresnel, PBR, noise patterns)
- [ ] Refactoring: Extract method, Extract to composition
- [ ] Code metrics (complexity score, inheritance depth)

---

## ARCHITECTURE

```
VSCode/
├── vscode-extension/           # TypeScript VS Code extension
│   ├── src/
│   │   ├── extension.ts        # Entry point, LSP client setup
│   │   ├── ExternalShaderProvider.ts  # Read-only shader filesystem
│   │   └── panels/
│   │       └── UnifiedTreeProvider.ts # Sidebar TreeView
│   ├── syntaxes/
│   │   └── sdsl.tmLanguage.json
│   └── package.json
│
└── language-server/            # C# Language Server (.NET 8)
    ├── Program.cs              # Server entry, handler registration
    ├── Handlers/
    │   ├── TextDocumentSyncHandler.cs  # Open/change/close, diagnostics
    │   ├── CompletionHandler.cs
    │   ├── HoverHandler.cs
    │   ├── DefinitionHandler.cs
    │   ├── SignatureHelpHandler.cs
    │   ├── CodeActionHandler.cs
    │   ├── SemanticTokensHandler.cs
    │   ├── ShutdownHandler.cs
    │   └── PanelDataHandler.cs  # Custom LSP for sidebar data
    └── Services/
        ├── ShaderParser.cs      # Stride.Shaders.Parser wrapper
        ├── ShaderWorkspace.cs   # Index all shaders
        ├── InheritanceResolver.cs
        ├── CompletionService.cs
        ├── HlslTypeSystem.cs
        ├── SemanticValidator.cs
        └── StrideInternalsAccessor.cs
```

### Key Dependencies
```xml
<PackageReference Include="OmniSharp.Extensions.LanguageServer" Version="0.19.9" />
<PackageReference Include="Stride.Shaders.Parser" Version="4.2.0.2188" />
```

---

## METRICS & GOALS

| Metric | Current | Target |
|--------|---------|--------|
| Time to first completion | ~200ms | < 100ms |
| Go-to-definition accuracy | 95% | 100% |
| Completion relevance | Good | Top suggestion correct 80%+ |
| Diagnostics delay | 2s (configurable) | User preference |
| Shutdown clean | Yes | No hanging processes |

---

## THE VISION

**"Spaceship-class" shader development:**

1. **Open** `MyShader.sdsl` -> Sidebar shows full inheritance context
2. **Type** `shader MyShader : ` -> Completions suggest best base shaders
3. **Write** a method -> See all inherited variables, streams, methods
4. **Hover** over anything -> See where it comes from, full override chain
5. **F12** on base shader -> Jump directly to its source
6. **Debug** -> Set breakpoints in SDSL, see original code (future)
7. **Ask AI** "add normal mapping" -> Context-aware code generation (future)

**This is not just an editor. It's a shader cockpit.**

---

*Progress tracking document for Stride SDSL Shader Tools VS Code Extension*
