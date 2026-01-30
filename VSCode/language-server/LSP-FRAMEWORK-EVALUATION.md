# LSP Framework Evaluation (January 2026)

## Context

This document evaluates whether to migrate from `OmniSharp.Extensions.LanguageServer` to a more modern approach using Microsoft's official packages.

**Current stack:** OmniSharp.Extensions.LanguageServer 0.19.9

**Alternative stack:**
- `Microsoft.VisualStudio.LanguageServer.Protocol` - Official LSP type definitions
- `StreamJsonRpc` - High-performance JSON-RPC 2.0 library

---

## Option A: OmniSharp.Extensions.LanguageServer

### Overview

OmniSharp is a framework that provides handler base classes, automatic capability negotiation, and built-in DI integration. It abstracts away most LSP plumbing.

### Current Usage

**Package reference:**
```xml
<PackageReference Include="OmniSharp.Extensions.LanguageServer" Version="0.19.9" />
```

**Server startup (Program.cs):**
```csharp
var server = await LanguageServer.From(options =>
    options
        .WithInput(Console.OpenStandardInput())
        .WithOutput(Console.OpenStandardOutput())
        .ConfigureLogging(builder => builder
            .AddLanguageProtocolLogging()
            .SetMinimumLevel(args.Contains("--debug") ? LogLevel.Debug : LogLevel.Information))
        .WithServices(ConfigureServices)
        .WithHandler<TextDocumentSyncHandler>()
        .WithHandler<CompletionHandler>()
        .WithHandler<HoverHandler>()
        .WithHandler<DefinitionHandler>()
        .WithHandler<SignatureHelpHandler>()
        .WithHandler<CodeActionHandler>()
        .WithHandler<SemanticTokensHandler>()
        .OnInitialize((server, request, token) =>
        {
            _workspace = server.Services.GetRequiredService<ShaderWorkspace>();
            // ... initialization logic
            return Task.CompletedTask;
        })
        .OnInitialized((server, request, response, token) =>
        {
            // Register custom JSON-RPC handlers
            server.Register(registry =>
            {
                registry.OnJsonRequest("stride/getInheritanceTree", (JToken token) =>
                {
                    var request = token.ToObject<InheritanceTreeParams>()!;
                    return Task.FromResult(JToken.FromObject(HandleInheritanceTreeRequest(request)));
                });
            });
            return Task.CompletedTask;
        })
).ConfigureAwait(false);

await server.WaitForExit.ConfigureAwait(false);
```

**Handler pattern (HoverHandler.cs):**
```csharp
public class HoverHandler : HoverHandlerBase
{
    private readonly ILogger<HoverHandler> _logger;
    private readonly ShaderWorkspace _workspace;

    public HoverHandler(ILogger<HoverHandler> logger, ShaderWorkspace workspace)
    {
        _logger = logger;
        _workspace = workspace;
    }

    public override Task<Hover?> Handle(HoverParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri;
        var position = request.Position;

        // ... hover logic ...

        return Task.FromResult<Hover?>(new Hover
        {
            Contents = new MarkedStringsOrMarkupContent(new MarkupContent
            {
                Kind = MarkupKind.Markdown,
                Value = markdown
            })
        });
    }

    protected override HoverRegistrationOptions CreateRegistrationOptions(
        HoverCapability capability,
        ClientCapabilities clientCapabilities)
    {
        return new HoverRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForPattern("**/*.sdsl")
        };
    }
}
```

**Publishing diagnostics:**
```csharp
_server.TextDocument.PublishDiagnostics(new PublishDiagnosticsParams
{
    Uri = uri,
    Diagnostics = new Container<Diagnostic>(diagnostics)
});
```

### Pros

- Minimal boilerplate - handlers inherit base classes
- Automatic capability negotiation via `CreateRegistrationOptions()`
- Built-in DI with `Microsoft.Extensions.DependencyInjection`
- Type-safe protocol models (`Hover`, `CompletionItem`, `Diagnostic`, etc.)
- Lifecycle hooks (`OnInitialize`, `OnInitialized`)
- Easy custom JSON-RPC methods via `registry.OnJsonRequest()`
- Used by Azure Bicep Language Server (Microsoft production)

### Cons

- Heavy dependency (~4MB)
- Community concerns about long-term maintenance ([Issue #1221](https://github.com/OmniSharp/csharp-language-server-protocol/issues/1221))
- C# VSCode extension moved away from OmniSharp
- Opinionated - less control over JSON-RPC handling
- Uses Newtonsoft.Json (older) rather than System.Text.Json

### Maintenance Status

- Latest release: 0.19.8 (January 2026)
- Repository is active with recent commits
- .NET Foundation support planned but not finalized
- Bicep team continues to use and contribute

---

## Option B: Microsoft.VisualStudio.LanguageServer.Protocol + StreamJsonRpc

### Overview

The "modern DIY" approach assembles modular Microsoft components:
- **Protocol package** provides LSP type definitions (DTOs)
- **StreamJsonRpc** handles JSON-RPC 2.0 communication
- You wire everything together manually

This is the approach used by PowerShell and some internal Microsoft teams.

### Package References

```xml
<PackageReference Include="Microsoft.VisualStudio.LanguageServer.Protocol" Version="17.2.8" />
<PackageReference Include="StreamJsonRpc" Version="2.24.84" />
<PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="8.0.0" />
```

### Server Startup Pattern

```csharp
class Program
{
    static async Task Main(string[] args)
    {
        // Setup streams
        using var inputStream = Console.OpenStandardInput();
        using var outputStream = Console.OpenStandardOutput();

        // Create message handler (LSP uses header-delimited messages)
        var messageHandler = new HeaderDelimitedMessageHandler(inputStream, outputStream);

        // Create JSON-RPC connection
        using var jsonRpc = new JsonRpc(messageHandler);

        // Setup DI
        var services = new ServiceCollection();
        services.AddSingleton<ShaderWorkspace>();
        services.AddSingleton<InheritanceResolver>();
        // ... etc
        var serviceProvider = services.BuildServiceProvider();

        // Register handlers as RPC targets
        var server = new LanguageServerTarget(serviceProvider);
        jsonRpc.AddLocalRpcTarget(server, new JsonRpcTargetOptions
        {
            MethodNameTransform = CommonMethodNameTransforms.CamelCase
        });

        // Start listening
        jsonRpc.StartListening();

        // Wait for shutdown
        await jsonRpc.Completion;
    }
}
```

### Handler Class Pattern

```csharp
public class LanguageServerTarget
{
    private readonly IServiceProvider _services;
    private readonly ConcurrentDictionary<string, string> _documents = new();

    public LanguageServerTarget(IServiceProvider services)
    {
        _services = services;
    }

    // Initialize request
    [JsonRpcMethod("initialize")]
    public InitializeResult Initialize(InitializeParams @params)
    {
        // Process workspace folders
        if (@params.WorkspaceFolders != null)
        {
            var workspace = _services.GetRequiredService<ShaderWorkspace>();
            foreach (var folder in @params.WorkspaceFolders)
            {
                workspace.AddWorkspaceFolder(folder.Uri.LocalPath);
            }
        }

        // Return capabilities manually
        return new InitializeResult
        {
            Capabilities = new ServerCapabilities
            {
                TextDocumentSync = new TextDocumentSyncOptions
                {
                    OpenClose = true,
                    Change = TextDocumentSyncKind.Full,
                    Save = new SaveOptions { IncludeText = true }
                },
                CompletionProvider = new CompletionOptions
                {
                    TriggerCharacters = new[] { ".", ":", "<" },
                    ResolveProvider = true
                },
                HoverProvider = true,
                DefinitionProvider = true,
                SignatureHelpProvider = new SignatureHelpOptions
                {
                    TriggerCharacters = new[] { "(", "," }
                },
                CodeActionProvider = new CodeActionOptions
                {
                    CodeActionKinds = new[] { CodeActionKind.QuickFix }
                },
                SemanticTokensOptions = new SemanticTokensOptions
                {
                    Full = true,
                    Legend = new SemanticTokensLegend
                    {
                        TokenTypes = new[] { "type", "class", "struct", "interface" },
                        TokenModifiers = new[] { "declaration", "definition" }
                    }
                }
            }
        };
    }

    [JsonRpcMethod("initialized")]
    public void Initialized()
    {
        // Start background indexing
        Task.Run(() => _services.GetRequiredService<ShaderWorkspace>().IndexAllShaders());
    }

    // Document sync
    [JsonRpcMethod("textDocument/didOpen")]
    public void DidOpenTextDocument(DidOpenTextDocumentParams @params)
    {
        _documents[@params.TextDocument.Uri.ToString()] = @params.TextDocument.Text;
        PublishDiagnostics(@params.TextDocument.Uri);
    }

    [JsonRpcMethod("textDocument/didChange")]
    public void DidChangeTextDocument(DidChangeTextDocumentParams @params)
    {
        var uri = @params.TextDocument.Uri.ToString();
        // For full sync, take the last change
        _documents[uri] = @params.ContentChanges.Last().Text;
        PublishDiagnosticsDebounced(@params.TextDocument.Uri);
    }

    [JsonRpcMethod("textDocument/didClose")]
    public void DidCloseTextDocument(DidCloseTextDocumentParams @params)
    {
        _documents.TryRemove(@params.TextDocument.Uri.ToString(), out _);
    }

    // Hover
    [JsonRpcMethod("textDocument/hover")]
    public Hover? Hover(HoverParams @params)
    {
        var uri = @params.TextDocument.Uri.ToString();
        if (!_documents.TryGetValue(uri, out var content))
            return null;

        var position = @params.Position;
        var workspace = _services.GetRequiredService<ShaderWorkspace>();

        // ... hover logic (same as current implementation) ...

        return new Hover
        {
            Contents = new MarkupContent
            {
                Kind = MarkupKind.Markdown,
                Value = markdown
            }
        };
    }

    // Completion
    [JsonRpcMethod("textDocument/completion")]
    public CompletionList Completion(CompletionParams @params)
    {
        // ... completion logic ...
        return new CompletionList
        {
            IsIncomplete = false,
            Items = items.ToArray()
        };
    }

    // Definition
    [JsonRpcMethod("textDocument/definition")]
    public Location[]? Definition(TextDocumentPositionParams @params)
    {
        // ... definition logic ...
        return new[] { new Location { Uri = targetUri, Range = range } };
    }

    // Custom requests
    [JsonRpcMethod("stride/getInheritanceTree")]
    public InheritanceTreeResponse GetInheritanceTree(InheritanceTreeParams @params)
    {
        // ... custom logic ...
        return response;
    }

    // Publishing diagnostics requires the JsonRpc instance
    private JsonRpc? _rpc;

    public void SetJsonRpc(JsonRpc rpc) => _rpc = rpc;

    private void PublishDiagnostics(Uri uri)
    {
        var diagnostics = ComputeDiagnostics(uri);
        _rpc?.NotifyAsync("textDocument/publishDiagnostics", new PublishDiagnosticParams
        {
            Uri = uri,
            Diagnostics = diagnostics.ToArray()
        });
    }
}
```

### Pros

- Official Microsoft packages
- Lightweight dependencies
- Full control over JSON-RPC handling
- Uses modern .NET patterns
- StreamJsonRpc is actively maintained (2.24.84)
- Potentially faster startup (less abstraction)
- System.Text.Json support available

### Cons

- Significant boilerplate for each handler
- Manual capability registration in `Initialize`
- Manual document tracking (no `TextDocumentSyncHandlerBase`)
- No automatic capability negotiation
- No lifecycle hooks - must structure code differently
- Diagnostics publishing requires manual `JsonRpc.NotifyAsync()`
- `Microsoft.VisualStudio.LanguageServer.Protocol` is designed for VS extensions, not VS Code (some types may not match exactly)
- Less community documentation for LSP use cases

---

## Migration Effort Breakdown

### Files Requiring Changes

| File | Effort | Description |
|------|--------|-------------|
| `StrideShaderLanguageServer.csproj` | Low | Package reference changes |
| `Program.cs` | High | Complete rewrite of server startup |
| `TextDocumentSyncHandler.cs` | High | Merge into main target, manual sync |
| `CompletionHandler.cs` | Medium | Convert to `[JsonRpcMethod]` |
| `HoverHandler.cs` | Medium | Convert to `[JsonRpcMethod]` |
| `DefinitionHandler.cs` | Medium | Convert to `[JsonRpcMethod]` |
| `SignatureHelpHandler.cs` | Medium | Convert to `[JsonRpcMethod]` |
| `CodeActionHandler.cs` | Medium | Convert to `[JsonRpcMethod]` |
| `SemanticTokensHandler.cs` | High | Complex token legend registration |

### New Files Required

- `LanguageServerTarget.cs` - Main handler class with all `[JsonRpcMethod]` methods
- Possibly `LspTypeExtensions.cs` - Helpers if MS types don't match OmniSharp types exactly

### Code That Stays the Same

The business logic is decoupled and would not change:
- `Services/ShaderWorkspace.cs`
- `Services/InheritanceResolver.cs`
- `Services/ShaderParser.cs`
- `Services/CompletionService.cs`
- `Services/SemanticValidator.cs`
- `Services/HlslTypeSystem.cs`
- `Services/ParsedShader.cs`

---

## Comparison Summary

| Aspect | OmniSharp | DIY (MS + StreamJsonRpc) |
|--------|-----------|--------------------------|
| **Package size** | ~4MB | ~1MB |
| **Boilerplate** | Low | High |
| **Control** | Medium | Full |
| **DI integration** | Built-in | Manual |
| **Capability negotiation** | Automatic | Manual |
| **Document sync** | Base class | Manual dictionary |
| **Diagnostics** | `_server.TextDocument.PublishDiagnostics()` | `_rpc.NotifyAsync()` |
| **Custom methods** | `registry.OnJsonRequest()` | `[JsonRpcMethod]` |
| **Serialization** | Newtonsoft.Json | System.Text.Json option |
| **Maintenance** | Active (Bicep uses it) | Official MS packages |

---

## Decision Factors

### Reasons to stay with OmniSharp:
- Working implementation exists
- Bicep validates the approach
- Business logic is already decoupled (easy to migrate later if needed)
- Focus can remain on features (Phase 3-7)

### Reasons to migrate:
- Prefer official Microsoft packages
- Want smaller dependency footprint
- Need full control over JSON-RPC handling
- Concerned about OmniSharp long-term maintenance
- Want System.Text.Json instead of Newtonsoft

### If migrating later:
1. Business logic (ShaderWorkspace, InheritanceResolver, etc.) stays unchanged
2. Only handler "shell" code needs rewriting
3. Can migrate incrementally by method

---

## Sources

- [OmniSharp/csharp-language-server-protocol](https://github.com/OmniSharp/csharp-language-server-protocol) - GitHub repo
- [OmniSharp Issue #1221](https://github.com/OmniSharp/csharp-language-server-protocol/issues/1221) - Maintenance concerns
- [Azure/bicep](https://github.com/Azure/bicep) - Uses OmniSharp
- [microsoft/vs-streamjsonrpc](https://github.com/microsoft/vs-streamjsonrpc) - StreamJsonRpc repo
- [StreamJsonRpc Documentation](https://microsoft.github.io/vs-streamjsonrpc/) - Official docs
- [StreamJsonRpc.Sample](https://github.com/AArnott/StreamJsonRpc.Sample) - Example project
- [Microsoft.VisualStudio.LanguageServer.Protocol](https://www.nuget.org/packages/Microsoft.VisualStudio.LanguageServer.Protocol/) - NuGet
- [LSP Specification 3.17](https://microsoft.github.io/language-server-protocol/specifications/lsp/3.17/specification/)

---

*Evaluation performed January 2026*
