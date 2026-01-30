import * as vscode from 'vscode';
import type { LanguageClient } from 'vscode-languageclient/node';

export interface ShaderNode {
    name: string;
    filePath: string;
    source: string;
    line: number;
    isLocal: boolean;
    isCurrentShader?: boolean;
}

export interface InheritanceTreeResponse {
    currentShader: ShaderNode | null;
    baseShaders: ShaderNode[];
}

export class InheritanceTreeProvider implements vscode.TreeDataProvider<ShaderNode> {
    private _onDidChangeTreeData = new vscode.EventEmitter<ShaderNode | undefined>();
    readonly onDidChangeTreeData = this._onDidChangeTreeData.event;

    private cachedData: InheritanceTreeResponse | null = null;
    private currentUri: string | null = null;

    constructor(private client: LanguageClient | undefined) {}

    setClient(client: LanguageClient) {
        this.client = client;
    }

    refresh(): void {
        this.cachedData = null;
        this._onDidChangeTreeData.fire(undefined);
    }

    getTreeItem(element: ShaderNode): vscode.TreeItem {
        const item = new vscode.TreeItem(
            element.name,
            element.isCurrentShader
                ? vscode.TreeItemCollapsibleState.Expanded
                : vscode.TreeItemCollapsibleState.None
        );

        // Description shows the source (Stride, vvvv, Workspace)
        item.description = element.source;

        // Tooltip shows full path
        item.tooltip = new vscode.MarkdownString();
        item.tooltip.appendMarkdown(`**${element.name}**\n\n`);
        item.tooltip.appendMarkdown(`Source: ${element.source}\n\n`);
        item.tooltip.appendMarkdown(`\`${element.filePath}\``);

        // Icon based on whether it's the current shader or inherited
        if (element.isCurrentShader) {
            item.iconPath = new vscode.ThemeIcon('symbol-class');
        } else {
            item.iconPath = new vscode.ThemeIcon('symbol-interface');
        }

        // Context value for potential context menu actions
        item.contextValue = element.isCurrentShader ? 'currentShader' : 'baseShader';

        // Command to open the shader file
        if (!element.isCurrentShader) {
            item.command = {
                command: 'strideShaderTools.openShader',
                title: 'Open Shader',
                arguments: [element.filePath, element.line]
            };
        }

        return item;
    }

    async getChildren(element?: ShaderNode): Promise<ShaderNode[]> {
        if (!this.client) {
            console.log('[InheritanceTree] No client available');
            return [];
        }

        const activeEditor = vscode.window.activeTextEditor;
        if (!activeEditor || activeEditor.document.languageId !== 'sdsl') {
            console.log('[InheritanceTree] No active SDSL editor');
            return [];
        }

        const uri = activeEditor.document.uri.toString();

        // If we're at the root level, return current shader + base shaders
        if (!element) {
            try {
                // Check if we need to refresh
                if (this.currentUri !== uri || !this.cachedData) {
                    this.currentUri = uri;
                    console.log('[InheritanceTree] Requesting data for:', uri);
                    const rawResponse = await this.client.sendRequest<InheritanceTreeResponse>(
                        'stride/getInheritanceTree',
                        { uri }
                    );
                    console.log('[InheritanceTree] Raw response:', JSON.stringify(rawResponse, null, 2));
                    this.cachedData = rawResponse;
                }

                const result: ShaderNode[] = [];

                // Add current shader first (check both camelCase and PascalCase)
                const currentShader = this.cachedData?.currentShader ?? (this.cachedData as any)?.CurrentShader;
                if (currentShader) {
                    result.push({
                        ...currentShader,
                        isCurrentShader: true
                    });
                } else {
                    console.log('[InheritanceTree] No currentShader in response');
                }

                return result;
            } catch (error) {
                console.error('[InheritanceTree] Failed to get inheritance tree:', error);
                return [];
            }
        }

        // If element is the current shader, return its base shaders
        const baseShaders = this.cachedData?.baseShaders ?? (this.cachedData as any)?.BaseShaders;
        if (element.isCurrentShader && baseShaders) {
            console.log('[InheritanceTree] Returning', baseShaders.length, 'base shaders');
            return baseShaders.map((shader: ShaderNode) => ({
                ...shader,
                isCurrentShader: false
            }));
        }

        return [];
    }

    getParent(_element: ShaderNode): vscode.ProviderResult<ShaderNode> {
        // Not needed for our flat-ish tree structure
        return null;
    }
}
