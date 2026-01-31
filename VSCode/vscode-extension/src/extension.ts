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
    EXTERNAL_SHADER_SCHEME,
    ExternalShaderProvider,
    createExternalShaderUri,
} from './ExternalShaderProvider';
import { UnifiedTreeProvider } from './panels';

const EXTENSION_ID = 'tebjan.stride-shader-tools';

let client: LanguageClient | undefined;

// Unified TreeView provider (initialized after language server starts)
let unifiedTreeProvider: UnifiedTreeProvider;

// Debounce timeout for panel refresh (typed properly to avoid 'any' on globalThis)
let refreshTimeout: ReturnType<typeof setTimeout> | undefined;

// Regex to detect each "Add: ShaderName" pattern in hover content (global)
const ADD_SHADER_REGEX = /Add:\s+(\w+)/g;

// Regex to detect each "Remove: ShaderName" pattern in hover content (global)
const REMOVE_SHADER_REGEX = /Remove:\s+(\w+)/g;

// Regex to detect "RenameFile: newName.sdsl|oldPath|newPath" pattern
const RENAME_FILE_REGEX = /RenameFile:\s+([^|]+)\|([^|]+)\|(.+)/g;

// Regex to detect "RenameShader: newName" pattern
const RENAME_SHADER_REGEX = /RenameShader:\s+(\w+)/g;

// Regex to detect "OpenFile: displayPath|fullPath|line" pattern for clickable file paths
const OPEN_FILE_REGEX = /OpenFile:\s+([^|]+)\|([^|]+)\|(\d+)/g;

// Interface for dotnet.acquire result
interface IDotnetAcquireResult {
    dotnetPath: string;
}

export async function activate(context: vscode.ExtensionContext) {
    console.log('Stride Shader Tools is activating...');

    // Register file system provider for external (read-only) shaders
    const externalShaderProvider = new ExternalShaderProvider();
    context.subscriptions.push(
        vscode.workspace.registerFileSystemProvider(
            EXTERNAL_SHADER_SCHEME,
            externalShaderProvider,
            {
                isReadonly: true,
                isCaseSensitive: true,
            }
        )
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
        vscode.commands.registerCommand(
            'strideShaderTools.openShader',
            async (filePath: string, line?: number) => {
                if (typeof filePath !== 'string') {
                    return;
                }
                await openShaderFile(filePath, line);
            }
        )
    );

    // Command for document link clicks (direct click on shader names in code)
    // Receives encoded args: "filePath|isWorkspaceShader"
    context.subscriptions.push(
        vscode.commands.registerCommand(
            'strideShaderTools.openShaderLink',
            async (encodedArgs: string) => {
                try {
                    const args = decodeURIComponent(encodedArgs);
                    const [filePath, isWorkspaceStr] = args.split('|');
                    const isWorkspaceShader =
                        isWorkspaceStr === 'true' || isWorkspaceStr === 'True';
                    await openShaderFile(filePath, undefined, isWorkspaceShader);
                } catch (error) {
                    console.error('Failed to open shader link:', error);
                }
            }
        )
    );

    // Command to refresh all panels
    context.subscriptions.push(
        vscode.commands.registerCommand('strideShaderTools.refreshPanels', () => {
            unifiedTreeProvider?.refresh();
        })
    );

    context.subscriptions.push(
        vscode.commands.registerCommand('strideShaderTools.showInheritanceTree', () => {
            // Focus on the unified shader context panel
            vscode.commands.executeCommand('strideShaderContext.focus');
        })
    );

    // Initialize unified TreeView provider (it will get the client later)
    unifiedTreeProvider = new UnifiedTreeProvider(undefined);

    // Register unified TreeView and listen for expansion state changes
    const treeView = vscode.window.createTreeView('strideShaderContext', {
        treeDataProvider: unifiedTreeProvider,
        showCollapseAll: true,
    });
    context.subscriptions.push(treeView);

    // Track expansion state changes
    context.subscriptions.push(
        treeView.onDidExpandElement((e) => {
            const nodeId = getNodeIdFromElement(e.element);
            if (nodeId) {
                unifiedTreeProvider.onNodeExpansionChanged(nodeId, false);
            }
        }),
        treeView.onDidCollapseElement((e) => {
            const nodeId = getNodeIdFromElement(e.element);
            if (nodeId) {
                unifiedTreeProvider.onNodeExpansionChanged(nodeId, true);
            }
        })
    );

    // Refresh panel when active editor changes to an SDSL file
    context.subscriptions.push(
        vscode.window.onDidChangeActiveTextEditor((editor) => {
            if (editor?.document.languageId === 'sdsl') {
                unifiedTreeProvider.refresh();
            }
        })
    );

    // Soft refresh panel when document content changes (preserves tree expansion)
    context.subscriptions.push(
        vscode.workspace.onDidChangeTextDocument((event) => {
            if (
                event.document.languageId === 'sdsl' &&
                event.document === vscode.window.activeTextEditor?.document
            ) {
                // Debounce: only refresh after user stops typing
                clearTimeout(refreshTimeout);
                refreshTimeout = setTimeout(() => {
                    // Use soft refresh to preserve tree expansion state
                    unifiedTreeProvider.softRefresh();
                }, 500); // 500ms debounce
            }
        })
    );

    // Command to add a base shader to the current file's shader declaration
    context.subscriptions.push(
        vscode.commands.registerCommand(
            'strideShaderTools.addBaseShader',
            async (shaderName: string) => {
                const editor = vscode.window.activeTextEditor;
                if (!editor || editor.document.languageId !== 'sdsl') {
                    return;
                }

                const document = editor.document;
                const text = document.getText();

                // Find the shader declaration line: "shader Name<...> : Base1, Base2 {" or "shader Name {"
                // Capture groups: 1=shader+name+optional template params, 2=colon if present, 3=base shaders, 4=whitespace before brace, 5=brace
                const shaderDeclRegex =
                    /^(\s*shader\s+\w+(?:<[^>]+>)?)(\s*:\s*)?([\w\s,<>]*?)(\s*)(\{)/m;
                const match = shaderDeclRegex.exec(text);

                if (!match) {
                    vscode.window.showWarningMessage(
                        'Could not find shader declaration in this file.'
                    );
                    return;
                }

                const shaderPart = match[1]; // "shader MyShader"
                const basesPart = match[3].trim(); // "Base1, Base2" or ""
                const whitespace = match[4]; // whitespace before { (may include newline)
                const brace = match[5]; // "{"

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
                await editor.edit((editBuilder) => {
                    editBuilder.replace(range, newDeclaration);
                });

                vscode.window.showInformationMessage(`Added base shader: ${shaderName}`);
            }
        )
    );

    // Command to remove a base shader from the current file's shader declaration
    context.subscriptions.push(
        vscode.commands.registerCommand(
            'strideShaderTools.removeBaseShader',
            async (shaderName: string) => {
                const editor = vscode.window.activeTextEditor;
                if (!editor || editor.document.languageId !== 'sdsl') {
                    return;
                }

                const document = editor.document;
                const text = document.getText();

                // Find the shader declaration line: "shader Name<...> : Base1, Base2 {"
                // Capture groups: 1=shader+name+optional template params, 2=colon+space, 3=base shaders, 4=whitespace before brace, 5=brace
                const shaderDeclRegex =
                    /^(\s*shader\s+\w+(?:<[^>]+>)?)(\s*:\s*)([\w\s,<>]+?)(\s*)(\{)/m;
                const match = shaderDeclRegex.exec(text);

                if (!match) {
                    vscode.window.showWarningMessage(
                        'Could not find shader declaration with base shaders.'
                    );
                    return;
                }

                const shaderPart = match[1]; // "shader MyShader"
                const basesPart = match[3].trim(); // "Base1, Base2"
                const whitespace = match[4]; // whitespace before {
                const brace = match[5]; // "{"

                // Parse base shaders
                const bases = basesPart
                    .split(',')
                    .map((s) => s.trim())
                    .filter((s) => s);

                // Remove the target shader (case-insensitive match)
                const newBases = bases.filter((b) => b.toLowerCase() !== shaderName.toLowerCase());

                if (newBases.length === bases.length) {
                    vscode.window.showWarningMessage(
                        `Base shader '${shaderName}' not found in declaration.`
                    );
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
                await editor.edit((editBuilder) => {
                    editBuilder.replace(range, newDeclaration);
                });

                vscode.window.showInformationMessage(`Removed base shader: ${shaderName}`);
            }
        )
    );

    // Command to rename a shader file (used by quick fix for filename mismatch)
    context.subscriptions.push(
        vscode.commands.registerCommand(
            'strideShaderTools.renameFile',
            async (oldPath: string, newPath: string) => {
                const oldUri = vscode.Uri.file(oldPath);
                const newUri = vscode.Uri.file(newPath);
                const edit = new vscode.WorkspaceEdit();
                edit.renameFile(oldUri, newUri);
                await vscode.workspace.applyEdit(edit);
            }
        )
    );

    // Command to rename the shader declaration in the current file
    context.subscriptions.push(
        vscode.commands.registerCommand(
            'strideShaderTools.renameShaderInFile',
            async (newName: string) => {
                const editor = vscode.window.activeTextEditor;
                if (!editor || editor.document.languageId !== 'sdsl') {
                    return;
                }

                const text = editor.document.getText();
                // Find "shader OldName" and replace with "shader NewName"
                const shaderDeclRegex = /(\bshader\s+)(\w+)(\s*[:<{])/;
                const match = shaderDeclRegex.exec(text);

                if (!match) {
                    vscode.window.showWarningMessage('Could not find shader declaration.');
                    return;
                }

                const startOffset = match.index + match[1].length;
                const endOffset = startOffset + match[2].length;
                const startPos = editor.document.positionAt(startOffset);
                const endPos = editor.document.positionAt(endOffset);

                await editor.edit((editBuilder) => {
                    editBuilder.replace(new vscode.Range(startPos, endPos), newName);
                });
            }
        )
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
            return result.dotnetPath;
        }
    } catch {
        // Failed to acquire runtime, will fall back to system dotnet
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
        serverOptions = createProjectServerOptions(devProjectPath);
    }

    // Get additional shader paths from config
    const additionalPaths = config.get<string[]>('shaderPaths') || [];
    const diagnosticsDelay = config.get<number>('diagnostics.delay') || 2000;

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
            diagnosticsDelayMs: diagnosticsDelay,
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
            // Intercept definition requests to use our openShaderFile for consistent behavior
            // This ensures workspace shaders open as editable, external shaders as read-only
            provideDefinition: async (document, position, token, next) => {
                const result = await next(document, position, token);
                if (!result) return result;

                // Extract the first location from the result
                let targetUri: vscode.Uri | undefined;
                let targetLine: number | undefined;

                if (Array.isArray(result)) {
                    const first = result[0];
                    if (first) {
                        if ('targetUri' in first) {
                            // DefinitionLink
                            targetUri = first.targetUri;
                            targetLine = first.targetRange.start.line + 1;
                        } else if ('uri' in first) {
                            // Location
                            targetUri = first.uri;
                            targetLine = first.range.start.line + 1;
                        }
                    }
                } else if ('uri' in result) {
                    targetUri = result.uri;
                    targetLine = result.range.start.line + 1;
                }

                if (targetUri && targetUri.scheme === 'file') {
                    // Use our openShaderFile which handles workspace vs external consistently
                    // Workspace: editable, pinned | External: read-only, preview
                    await openShaderFile(targetUri.fsPath, targetLine);
                    return null;
                }

                // For non-file URIs, let VS Code handle it
                return result;
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
        await client.start();

        // Set the client on the unified TreeView provider now that it's ready
        unifiedTreeProvider.setClient(client);

        // Initial refresh if there's an active SDSL editor
        if (vscode.window.activeTextEditor?.document.languageId === 'sdsl') {
            unifiedTreeProvider.refresh();
        }
    } catch (error) {
        console.error('Failed to start language server:', error);
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
    const transformContent = (
        content: vscode.MarkdownString | string | { language: string; value: string }
    ): vscode.MarkdownString => {
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

        // Replace "RenameFile: newName.sdsl|oldPath|newPath" with clickable link
        newText = newText.replace(
            RENAME_FILE_REGEX,
            (_match, newName: string, oldPath: string, newPath: string) => {
                const args = encodeURIComponent(JSON.stringify([oldPath.trim(), newPath.trim()]));
                const commandUri = `command:strideShaderTools.renameFile?${args}`;
                return `[Rename file to ${newName.trim()}](${commandUri})`;
            }
        );

        // Replace "RenameShader: newName" with clickable link
        newText = newText.replace(RENAME_SHADER_REGEX, (_match, newName: string) => {
            const args = encodeURIComponent(JSON.stringify([newName]));
            const commandUri = `command:strideShaderTools.renameShaderInFile?${args}`;
            return `[Rename shader to ${newName}](${commandUri})`;
        });

        // Replace "OpenFile: displayPath|fullPath|line" with clickable link
        newText = newText.replace(
            OPEN_FILE_REGEX,
            (_match, displayPath: string, fullPath: string, line: string) => {
                const args = encodeURIComponent(
                    JSON.stringify([fullPath.trim(), Number.parseInt(line, 10)])
                );
                const commandUri = `command:strideShaderTools.openShader?${args}`;
                return `[${displayPath.trim()}](${commandUri})`;
            }
        );

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
async function openShaderFile(
    filePath: string,
    line?: number,
    isWorkspaceShader?: boolean
): Promise<void> {
    try {
        // Determine if this is a workspace shader
        let isEditable = isWorkspaceShader;
        if (isEditable === undefined) {
            // Infer from workspace folders
            const workspaceFolders = vscode.workspace.workspaceFolders;
            if (workspaceFolders) {
                isEditable = workspaceFolders.some((folder) =>
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
        // Workspace files: editable, pinned tab
        // External files: read-only, preview mode (can be replaced by next navigation)
        const editor = await vscode.window.showTextDocument(doc, {
            viewColumn: vscode.ViewColumn.Active,
            preserveFocus: false,
            preview: !isEditable,
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
            vscode.window.setStatusBarMessage(
                `📖 ${shaderName} (External shader - read-only)`,
                5000
            );
        }
    } catch (error) {
        console.error('Failed to open shader file:', error);
        vscode.window.showErrorMessage(`Failed to open shader: ${filePath}`);
    }
}

/**
 * Helper function to get a node ID from a tree element.
 * This is a fallback when the element doesn't have an id property set.
 */
function getNodeIdFromElement(element: unknown): string | undefined {
    if (!element || typeof element !== 'object') return undefined;

    const el = element as Record<string, unknown>;

    if (el.type === 'root' && typeof el.shaderName === 'string') {
        return `root:${el.shaderName}`;
    }
    if (el.type === 'category' && typeof el.category === 'string') {
        return `category:${el.category}`;
    }
    if (el.type === 'shader' && typeof el.shader === 'object' && el.shader) {
        const shader = el.shader as Record<string, unknown>;
        return `shader:${shader.name}`;
    }
    if (
        el.type === 'member' &&
        typeof el.member === 'object' &&
        el.member &&
        typeof el.category === 'string'
    ) {
        const member = el.member as Record<string, unknown>;
        return `member:${el.category}:${member.sourceShader}:${member.name}`;
    }
    if (el.type === 'composition' && typeof el.composition === 'object' && el.composition) {
        const comp = el.composition as Record<string, unknown>;
        return `composition:${comp.sourceShader}:${comp.name}`;
    }

    return undefined;
}

export function deactivate(): Thenable<void> | undefined {
    // Clear any pending refresh timeout
    clearTimeout(refreshTimeout);

    if (!client) {
        return undefined;
    }
    return client.stop();
}
