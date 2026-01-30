import * as fs from 'node:fs';
import * as path from 'node:path';
import * as vscode from 'vscode';
import {
    LanguageClient,
    type LanguageClientOptions,
    type ServerOptions,
    TransportKind,
} from 'vscode-languageclient/node';
import {
    InheritanceTreeProvider,
    VariablesTreeProvider,
    MethodsTreeProvider,
    StreamsTreeProvider,
} from './panels';
import {
    ExternalShaderProvider,
    EXTERNAL_SHADER_SCHEME,
    createExternalShaderUri,
} from './ExternalShaderProvider';

const EXTENSION_ID = 'tebjan.stride-shader-tools';

let client: LanguageClient | undefined;

// TreeView providers (initialized after language server starts)
let inheritanceProvider: InheritanceTreeProvider;
let variablesProvider: VariablesTreeProvider;
let methodsProvider: MethodsTreeProvider;
let streamsProvider: StreamsTreeProvider;

// Regex to detect each "Add: ShaderName" pattern in hover content (global)
const ADD_SHADER_REGEX = /Add:\s+(\w+)/g;

// Regex to detect each "Remove: ShaderName" pattern in hover content (global)
const REMOVE_SHADER_REGEX = /Remove:\s+(\w+)/g;

// Interface for dotnet.acquire result
interface IDotnetAcquireResult {
    dotnetPath: string;
}

export async function activate(context: vscode.ExtensionContext) {
    console.log('Stride Shader Tools is activating...');

    // Register file system provider for external (read-only) shaders
    const externalShaderProvider = new ExternalShaderProvider();
    context.subscriptions.push(
        vscode.workspace.registerFileSystemProvider(EXTERNAL_SHADER_SCHEME, externalShaderProvider, {
            isReadonly: true,
            isCaseSensitive: true,
        })
    );

    // Register commands
    context.subscriptions.push(
        vscode.commands.registerCommand('strideShaderTools.restartServer', async () => {
            if (client) {
                await client.stop();
                await client.start();
                vscode.window.showInformationMessage('Stride Shader Language Server restarted.');
            }
        })
    );

    // Command to open a shader file (used by TreeView items and go-to-definition)
    // Non-workspace files (Stride/vvvv shaders) open as read-only
    context.subscriptions.push(
        vscode.commands.registerCommand('strideShaderTools.openShader', async (filePath: string, line?: number) => {
            await openShaderFile(filePath, line);
        })
    );

    // Command for document link clicks (direct click on shader names in code)
    // Receives encoded args: "filePath|isWorkspaceShader"
    context.subscriptions.push(
        vscode.commands.registerCommand('strideShaderTools.openShaderLink', async (encodedArgs: string) => {
            try {
                const args = decodeURIComponent(encodedArgs);
                const [filePath, isWorkspaceStr] = args.split('|');
                const isWorkspaceShader = isWorkspaceStr === 'true' || isWorkspaceStr === 'True';
                await openShaderFile(filePath, undefined, isWorkspaceShader);
            } catch (error) {
                console.error('Failed to open shader link:', error);
            }
        })
    );

    // Command to refresh all panels
    context.subscriptions.push(
        vscode.commands.registerCommand('strideShaderTools.refreshPanels', () => {
            inheritanceProvider?.refresh();
            variablesProvider?.refresh();
            methodsProvider?.refresh();
            streamsProvider?.refresh();
        })
    );

    context.subscriptions.push(
        vscode.commands.registerCommand('strideShaderTools.showInheritanceTree', () => {
            // Focus on the inheritance panel
            vscode.commands.executeCommand('strideInheritance.focus');
        })
    );

    // Initialize TreeView providers (they'll get the client later)
    inheritanceProvider = new InheritanceTreeProvider(undefined);
    variablesProvider = new VariablesTreeProvider(undefined);
    methodsProvider = new MethodsTreeProvider(undefined);
    streamsProvider = new StreamsTreeProvider(undefined);

    // Register TreeViews
    context.subscriptions.push(
        vscode.window.createTreeView('strideInheritance', {
            treeDataProvider: inheritanceProvider,
            showCollapseAll: true,
        })
    );
    context.subscriptions.push(
        vscode.window.createTreeView('strideStreams', {
            treeDataProvider: streamsProvider,
            showCollapseAll: true,
        })
    );
    context.subscriptions.push(
        vscode.window.createTreeView('strideVariables', {
            treeDataProvider: variablesProvider,
            showCollapseAll: true,
        })
    );
    context.subscriptions.push(
        vscode.window.createTreeView('strideMethods', {
            treeDataProvider: methodsProvider,
            showCollapseAll: true,
        })
    );

    // Refresh panels when active editor changes to an SDSL file
    context.subscriptions.push(
        vscode.window.onDidChangeActiveTextEditor(editor => {
            if (editor?.document.languageId === 'sdsl') {
                inheritanceProvider.refresh();
                variablesProvider.refresh();
                methodsProvider.refresh();
                streamsProvider.refresh();
            }
        })
    );

    // Refresh panels when document content changes (e.g., after adding base shader)
    context.subscriptions.push(
        vscode.workspace.onDidChangeTextDocument(event => {
            if (event.document.languageId === 'sdsl' &&
                event.document === vscode.window.activeTextEditor?.document) {
                // Debounce: only refresh after user stops typing
                clearTimeout((globalThis as any).__sdslRefreshTimeout);
                (globalThis as any).__sdslRefreshTimeout = setTimeout(() => {
                    inheritanceProvider.refresh();
                    variablesProvider.refresh();
                    methodsProvider.refresh();
                    streamsProvider.refresh();
                }, 500); // 500ms debounce
            }
        })
    );

    // Command to add a base shader to the current file's shader declaration
    context.subscriptions.push(
        vscode.commands.registerCommand('strideShaderTools.addBaseShader', async (shaderName: string) => {
            const editor = vscode.window.activeTextEditor;
            if (!editor || editor.document.languageId !== 'sdsl') {
                return;
            }

            const document = editor.document;
            const text = document.getText();

            // Find the shader declaration line: "shader Name : Base1, Base2 {" or "shader Name {"
            // Capture groups: 1=shader+name, 2=colon if present, 3=base shaders, 4=whitespace before brace, 5=brace
            const shaderDeclRegex = /^(\s*shader\s+\w+)(\s*:\s*)?([\w\s,<>]*?)(\s*)(\{)/m;
            const match = shaderDeclRegex.exec(text);

            if (!match) {
                vscode.window.showWarningMessage('Could not find shader declaration in this file.');
                return;
            }

            const shaderPart = match[1];           // "shader MyShader"
            const basesPart = match[3].trim();     // "Base1, Base2" or ""
            const whitespace = match[4];           // whitespace before { (may include newline)
            const brace = match[5];                // "{"

            let newDeclaration: string;
            if (!basesPart) {
                // No existing bases - add ": NewBase" on same line as shader name
                newDeclaration = `${shaderPart} : ${shaderName}${whitespace}${brace}`;
            } else {
                // Has existing bases - append ", NewBase"
                newDeclaration = `${shaderPart} : ${basesPart}, ${shaderName}${whitespace}${brace}`;
            }

            // Calculate the range to replace
            const startOffset = match.index;
            const endOffset = startOffset + match[0].length;
            const startPos = document.positionAt(startOffset);
            const endPos = document.positionAt(endOffset);
            const range = new vscode.Range(startPos, endPos);

            // Apply the edit
            await editor.edit(editBuilder => {
                editBuilder.replace(range, newDeclaration);
            });

            vscode.window.showInformationMessage(`Added base shader: ${shaderName}`);
        })
    );

    // Command to remove a base shader from the current file's shader declaration
    context.subscriptions.push(
        vscode.commands.registerCommand('strideShaderTools.removeBaseShader', async (shaderName: string) => {
            const editor = vscode.window.activeTextEditor;
            if (!editor || editor.document.languageId !== 'sdsl') {
                return;
            }

            const document = editor.document;
            const text = document.getText();

            // Find the shader declaration line: "shader Name : Base1, Base2 {"
            // Capture groups: 1=shader+name, 2=colon+space, 3=base shaders, 4=whitespace before brace, 5=brace
            const shaderDeclRegex = /^(\s*shader\s+\w+)(\s*:\s*)([\w\s,<>]+?)(\s*)(\{)/m;
            const match = shaderDeclRegex.exec(text);

            if (!match) {
                vscode.window.showWarningMessage('Could not find shader declaration with base shaders.');
                return;
            }

            const shaderPart = match[1];           // "shader MyShader"
            const basesPart = match[3].trim();     // "Base1, Base2"
            const whitespace = match[4];           // whitespace before {
            const brace = match[5];                // "{"

            // Parse base shaders
            const bases = basesPart.split(',').map(s => s.trim()).filter(s => s);

            // Remove the target shader (case-insensitive match)
            const newBases = bases.filter(b => b.toLowerCase() !== shaderName.toLowerCase());

            if (newBases.length === bases.length) {
                vscode.window.showWarningMessage(`Base shader '${shaderName}' not found in declaration.`);
                return;
            }

            let newDeclaration: string;
            if (newBases.length === 0) {
                // No bases left - remove the colon entirely
                newDeclaration = `${shaderPart}${whitespace}${brace}`;
            } else {
                // Still has bases - rebuild the list
                newDeclaration = `${shaderPart} : ${newBases.join(', ')}${whitespace}${brace}`;
            }

            // Calculate the range to replace
            const startOffset = match.index;
            const endOffset = startOffset + match[0].length;
            const startPos = document.positionAt(startOffset);
            const endPos = document.positionAt(endOffset);
            const range = new vscode.Range(startPos, endPos);

            // Apply the edit
            await editor.edit(editBuilder => {
                editBuilder.replace(range, newDeclaration);
            });

            vscode.window.showInformationMessage(`Removed base shader: ${shaderName}`);
        })
    );

    // Register a supplementary hover provider for diagnostics with clickable fix links
    context.subscriptions.push(
        vscode.languages.registerHoverProvider('sdsl', new DiagnosticHoverProvider())
    );

    // Start language server
    await startLanguageServer(context);

    console.log('Stride Shader Tools activated!');
}

/**
 * Acquires .NET runtime via the .NET Install Tool extension.
 * Returns the path to the dotnet executable, or undefined if acquisition fails.
 */
async function acquireDotNetRuntime(): Promise<string | undefined> {
    try {
        // Request .NET 8 runtime via the .NET Install Tool
        const result = await vscode.commands.executeCommand<IDotnetAcquireResult>(
            'dotnet.acquire',
            {
                version: '8.0',
                requestingExtensionId: EXTENSION_ID,
            }
        );

        if (result?.dotnetPath) {
            console.log('Acquired .NET runtime at:', result.dotnetPath);
            return result.dotnetPath;
        }
    } catch (error) {
        console.error('Failed to acquire .NET runtime:', error);
    }

    return undefined;
}

function createDllServerOptions(dotnetPath: string, dllPath: string): ServerOptions {
    return {
        run: {
            command: dotnetPath,
            args: [dllPath],
            transport: TransportKind.stdio,
        },
        debug: {
            command: dotnetPath,
            args: [dllPath, '--debug'],
            transport: TransportKind.stdio,
        },
    };
}

function createProjectServerOptions(projectPath: string): ServerOptions {
    // Development mode uses system dotnet (requires SDK)
    return {
        run: {
            command: 'dotnet',
            args: ['run', '--project', projectPath],
            transport: TransportKind.stdio,
        },
        debug: {
            command: 'dotnet',
            args: ['run', '--project', projectPath, '--', '--debug'],
            transport: TransportKind.stdio,
        },
    };
}

async function startLanguageServer(context: vscode.ExtensionContext): Promise<void> {
    const config = vscode.workspace.getConfiguration('strideShaderTools');

    // Get language server path from config
    const configuredPath = config.get<string>('languageServer.path');

    // Check for bundled server DLL (production mode)
    const bundledDll = path.join(context.extensionPath, 'server', 'StrideShaderLanguageServer.dll');
    const isProductionMode = fs.existsSync(bundledDll);

    let serverOptions: ServerOptions;

    if (configuredPath) {
        // User-configured path
        const dotnetPath = await acquireDotNetRuntime();
        if (!dotnetPath) {
            vscode.window.showErrorMessage(
                'Failed to acquire .NET 8 Runtime. Language server cannot start.'
            );
            return;
        }

        if (configuredPath.endsWith('.dll')) {
            serverOptions = createDllServerOptions(dotnetPath, configuredPath);
        } else {
            // Assume it's a project path - use system dotnet for development
            serverOptions = createProjectServerOptions(configuredPath);
        }
    } else if (isProductionMode) {
        // Production: use bundled DLL with acquired .NET runtime
        console.log('Using bundled language server:', bundledDll);

        const dotnetPath = await acquireDotNetRuntime();
        if (!dotnetPath) {
            vscode.window.showErrorMessage(
                'Failed to acquire .NET 8 Runtime. Language server cannot start. ' +
                'Please install .NET 8 Runtime manually from https://dotnet.microsoft.com/download/dotnet/8.0'
            );
            return;
        }

        serverOptions = createDllServerOptions(dotnetPath, bundledDll);
    } else {
        // Development: run from source (requires .NET SDK on PATH)
        const devProjectPath = path.join(context.extensionPath, '..', 'language-server');
        console.log('[LSP] Extension path:', context.extensionPath);
        console.log('[LSP] Dev project path:', devProjectPath);
        console.log('[LSP] Dev path exists:', fs.existsSync(devProjectPath));

        // Verify the project file exists
        const csprojPath = path.join(devProjectPath, 'StrideShaderLanguageServer.csproj');
        console.log('[LSP] .csproj exists:', fs.existsSync(csprojPath));

        serverOptions = createProjectServerOptions(devProjectPath);
    }

    // Get additional shader paths from config
    const additionalPaths = config.get<string[]>('shaderPaths') || [];

    // Client options with middleware to enhance hover with clickable links
    const clientOptions: LanguageClientOptions = {
        documentSelector: [
            { scheme: 'file', language: 'sdsl' },
            { scheme: EXTERNAL_SHADER_SCHEME, language: 'sdsl' },
        ],
        synchronize: {
            fileEvents: vscode.workspace.createFileSystemWatcher('**/*.sdsl'),
        },
        initializationOptions: {
            additionalShaderPaths: additionalPaths,
            workspaceFolders: vscode.workspace.workspaceFolders?.map((f) => f.uri.fsPath) || [],
        },
        outputChannelName: 'Stride Shader Language Server',
        middleware: {
            // Intercept hover responses to add clickable command links
            provideHover: async (document, position, token, next) => {
                const result = await next(document, position, token);
                if (!result) return result;

                // Transform the hover content to include clickable links
                return transformHoverWithClickableLinks(result);
            },
        },
    };

    // Create and start the client
    client = new LanguageClient(
        'strideShaderLanguageServer',
        'Stride Shader Language Server',
        serverOptions,
        clientOptions
    );

    try {
        console.log('[LSP] Starting language client...');
        await client.start();
        console.log('[LSP] Language server started successfully');

        // Check if client is actually ready
        const state = client.state;
        console.log('[LSP] Client state after start:', state);

        // Set the client on all TreeView providers now that it's ready
        inheritanceProvider.setClient(client);
        variablesProvider.setClient(client);
        methodsProvider.setClient(client);
        streamsProvider.setClient(client);
        console.log('[LSP] TreeView providers connected to client');

        // Initial refresh if there's an active SDSL editor
        if (vscode.window.activeTextEditor?.document.languageId === 'sdsl') {
            console.log('[LSP] Active SDSL editor found, refreshing panels');
            inheritanceProvider.refresh();
            variablesProvider.refresh();
            methodsProvider.refresh();
            streamsProvider.refresh();
        }
    } catch (error) {
        console.error('[LSP] Failed to start language server:', error);
        vscode.window.showWarningMessage(
            'Failed to start Stride Shader Language Server. IntelliSense may be limited. Check the Output panel for details.'
        );
    }

    context.subscriptions.push(client);
}

/**
 * Hover provider that adds clickable fix links for diagnostics.
 * Note: This provider now returns null as suggestions are handled by the language server
 * hover response and transformed by middleware.
 */
class DiagnosticHoverProvider implements vscode.HoverProvider {
    provideHover(
        _document: vscode.TextDocument,
        _position: vscode.Position,
        _token: vscode.CancellationToken
    ): vscode.ProviderResult<vscode.Hover> {
        // Suggestions are now in the hover response from the language server
        // and transformed by middleware in transformHoverWithClickableLinks
        return null;
    }
}

/**
 * Transform hover content to replace all "Add: ShaderName" patterns with clickable command links.
 * This allows users to click directly in the tooltip to add a base shader.
 */
function transformHoverWithClickableLinks(hover: vscode.Hover): vscode.Hover {
    const transformContent = (content: vscode.MarkdownString | string | { language: string; value: string }): vscode.MarkdownString => {
        let text: string;

        if (typeof content === 'string') {
            text = content;
        } else if (content instanceof vscode.MarkdownString) {
            text = content.value;
        } else if ('value' in content) {
            text = content.value;
        } else {
            return new vscode.MarkdownString(String(content));
        }

        // Replace all "Add: ShaderName" patterns with clickable links
        let newText = text.replace(ADD_SHADER_REGEX, (_match, shaderName: string) => {
            const args = encodeURIComponent(JSON.stringify([shaderName]));
            const commandUri = `command:strideShaderTools.addBaseShader?${args}`;
            return `[${shaderName}](${commandUri})`;
        });

        // Replace all "Remove: ShaderName" patterns with clickable links
        newText = newText.replace(REMOVE_SHADER_REGEX, (_match, shaderName: string) => {
            const args = encodeURIComponent(JSON.stringify([shaderName]));
            const commandUri = `command:strideShaderTools.removeBaseShader?${args}`;
            return `[Remove ${shaderName}](${commandUri})`;
        });

        const md = new vscode.MarkdownString(newText);
        md.isTrusted = true; // Required for command URIs to work
        return md;
    };

    // Transform all content items
    const newContents = hover.contents.map(transformContent);

    return new vscode.Hover(newContents, hover.range);
}

/**
 * Opens a shader file in the editor.
 * Workspace shaders open as editable, external shaders (Stride/vvvv) open as read-only
 * using a virtual file system provider.
 *
 * @param filePath - Path to the shader file
 * @param line - Optional line number to navigate to (1-based)
 * @param isWorkspaceShader - If provided, determines read-only mode. If undefined, infers from workspace folders.
 */
async function openShaderFile(filePath: string, line?: number, isWorkspaceShader?: boolean): Promise<void> {
    try {
        // Determine if this is a workspace shader
        let isEditable = isWorkspaceShader;
        if (isEditable === undefined) {
            // Infer from workspace folders
            const workspaceFolders = vscode.workspace.workspaceFolders;
            if (workspaceFolders) {
                isEditable = workspaceFolders.some(folder =>
                    filePath.toLowerCase().startsWith(folder.uri.fsPath.toLowerCase())
                );
            } else {
                isEditable = false;
            }
        }

        let uri: vscode.Uri;
        if (isEditable) {
            // Workspace shader - use regular file URI (editable)
            uri = vscode.Uri.file(filePath);
        } else {
            // External shader - use custom scheme (read-only)
            uri = createExternalShaderUri(filePath, line);
        }

        // Open the document
        const doc = await vscode.workspace.openTextDocument(uri);

        // Show the document
        const editor = await vscode.window.showTextDocument(doc, {
            viewColumn: vscode.ViewColumn.Active,
            preserveFocus: false,
            preview: !isEditable, // Preview mode for external files (can be replaced by next navigation)
        });

        // Navigate to specific line if provided
        if (line !== undefined && line > 0) {
            const lineIndex = line - 1; // Convert to 0-based
            const range = new vscode.Range(lineIndex, 0, lineIndex, 0);
            editor.revealRange(range, vscode.TextEditorRevealType.InCenter);
            editor.selection = new vscode.Selection(range.start, range.start);
        }

        // Show status message for external (read-only) shaders
        if (!isEditable) {
            const shaderName = path.basename(filePath, '.sdsl');
            vscode.window.setStatusBarMessage(`📖 ${shaderName} (External shader - read-only)`, 5000);
        }
    } catch (error) {
        console.error('Failed to open shader file:', error);
        vscode.window.showErrorMessage(`Failed to open shader: ${filePath}`);
    }
}

export function deactivate(): Thenable<void> | undefined {
    if (!client) {
        return undefined;
    }
    return client.stop();
}
