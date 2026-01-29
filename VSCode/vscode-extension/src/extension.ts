import { spawn } from 'node:child_process';
import * as path from 'node:path';
import * as vscode from 'vscode';
import {
    LanguageClient,
    type LanguageClientOptions,
    type ServerOptions,
    TransportKind,
} from 'vscode-languageclient/node';

let client: LanguageClient | undefined;

export async function activate(context: vscode.ExtensionContext) {
    console.log('Stride Shader Tools is activating...');

    // Check for .NET SDK
    const dotnetAvailable = await checkDotNetSdk();
    if (!dotnetAvailable) {
        const choice = await vscode.window.showErrorMessage(
            'Stride Shader Tools requires .NET 8 SDK. Please install it to enable IntelliSense.',
            'Download .NET 8',
            'Ignore'
        );
        if (choice === 'Download .NET 8') {
            vscode.env.openExternal(
                vscode.Uri.parse('https://dotnet.microsoft.com/download/dotnet/8.0')
            );
        }
        // Continue anyway - syntax highlighting will still work
    }

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

    // Start language server if .NET is available
    if (dotnetAvailable) {
        await startLanguageServer(context);
    }

    console.log('Stride Shader Tools activated!');
}

async function checkDotNetSdk(): Promise<boolean> {
    return new Promise((resolve) => {
        const process = spawn('dotnet', ['--list-sdks'], { shell: true });
        let output = '';

        process.stdout.on('data', (data) => {
            output += data.toString();
        });

        process.on('close', (code) => {
            if (code === 0 && output.includes('8.')) {
                resolve(true);
            } else {
                resolve(false);
            }
        });

        process.on('error', () => {
            resolve(false);
        });

        // Timeout after 5 seconds
        setTimeout(() => resolve(false), 5000);
    });
}

async function startLanguageServer(context: vscode.ExtensionContext): Promise<void> {
    const config = vscode.workspace.getConfiguration('strideShaderTools');

    // Get language server path from config or use default
    let serverPath = config.get<string>('languageServer.path');

    if (!serverPath) {
        // Default: look for language server in extension's sibling directory
        serverPath = path.join(
            context.extensionPath,
            '..',
            'language-server'
        );
    }

    // Server options - run via dotnet
    const serverOptions: ServerOptions = {
        run: {
            command: 'dotnet',
            args: ['run', '--project', serverPath],
            transport: TransportKind.stdio,
        },
        debug: {
            command: 'dotnet',
            args: ['run', '--project', serverPath, '--', '--debug'],
            transport: TransportKind.stdio,
        },
    };

    // Get additional shader paths from config
    const additionalPaths = config.get<string[]>('shaderPaths') || [];

    // Client options
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

export function deactivate(): Thenable<void> | undefined {
    if (!client) {
        return undefined;
    }
    return client.stop();
}
