import * as fs from 'node:fs';
import * as path from 'node:path';
import * as vscode from 'vscode';
import {
    LanguageClient,
    type LanguageClientOptions,
    type ServerOptions,
    TransportKind,
} from 'vscode-languageclient/node';
// Hover types are handled via vscode namespace

const EXTENSION_ID = 'tebjan.stride-shader-tools';

let client: LanguageClient | undefined;

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

    context.subscriptions.push(
        vscode.commands.registerCommand('strideShaderTools.showInheritanceTree', () => {
            vscode.window.showInformationMessage('Inheritance Tree panel coming soon!');
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
        console.log('Using development language server:', devProjectPath);
        serverOptions = createProjectServerOptions(devProjectPath);
    }

    // Get additional shader paths from config
    const additionalPaths = config.get<string[]>('shaderPaths') || [];

    // Client options with middleware to enhance hover with clickable links
    const clientOptions: LanguageClientOptions = {
        documentSelector: [{ scheme: 'file', language: 'sdsl' }],
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
        await client.start();
        console.log('Stride Shader Language Server started successfully');
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

export function deactivate(): Thenable<void> | undefined {
    if (!client) {
        return undefined;
    }
    return client.stop();
}
