import * as vscode from 'vscode';
import type { LanguageClient } from 'vscode-languageclient/node';
import type { MemberInfo, ShaderMembersResponse } from './VariablesTreeProvider';

export class MethodsTreeProvider implements vscode.TreeDataProvider<MemberInfo> {
    private _onDidChangeTreeData = new vscode.EventEmitter<MemberInfo | undefined>();
    readonly onDidChangeTreeData = this._onDidChangeTreeData.event;

    private cachedData: ShaderMembersResponse | null = null;
    private currentUri: string | null = null;

    constructor(private client: LanguageClient | undefined) {}

    setClient(client: LanguageClient) {
        this.client = client;
    }

    refresh(): void {
        this.cachedData = null;
        this._onDidChangeTreeData.fire(undefined);
    }

    getTreeItem(element: MemberInfo): vscode.TreeItem {
        // Format: returnType name(params)
        const methodLabel = `${element.type} ${element.name}${element.signature || '()'}`;
        const item = new vscode.TreeItem(methodLabel, vscode.TreeItemCollapsibleState.None);

        // Always show source shader as description (right-aligned, gray)
        item.description = element.sourceShader;

        // Build tooltip
        item.tooltip = new vscode.MarkdownString();
        item.tooltip.appendCodeblock(methodLabel, 'sdsl');
        item.tooltip.appendMarkdown(`\n\nDefined in: **${element.sourceShader}**`);
        if (element.comment) {
            item.tooltip.appendMarkdown(`\n\n${element.comment}`);
        }

        // Icon: local/override vs inherited
        item.iconPath = new vscode.ThemeIcon(
            element.isLocal ? 'symbol-method' : 'symbol-function'
        );

        item.contextValue = element.isLocal ? 'localMethod' : 'inheritedMethod';

        // Command to open file at method location
        if (element.filePath && element.line > 0) {
            item.command = {
                command: 'strideShaderTools.openShader',
                title: 'Go to Definition',
                arguments: [element.filePath, element.line]
            };
        }

        return item;
    }

    async getChildren(_element?: MemberInfo): Promise<MemberInfo[]> {
        if (!this.client) {
            return [];
        }

        const activeEditor = vscode.window.activeTextEditor;
        if (!activeEditor || activeEditor.document.languageId !== 'sdsl') {
            return [];
        }

        const uri = activeEditor.document.uri.toString();

        try {
            if (this.currentUri !== uri || !this.cachedData) {
                this.currentUri = uri;
                this.cachedData = await this.client.sendRequest<ShaderMembersResponse>(
                    'stride/getShaderMembers',
                    { uri }
                );
            }

            // Flatten all methods from groups into a single list
            // Sort: local methods first, then inherited (alphabetically within each)
            const allMethods: MemberInfo[] = [];
            for (const group of this.cachedData?.methods ?? []) {
                allMethods.push(...group.members);
            }

            // Sort: local first, then by name
            allMethods.sort((a, b) => {
                if (a.isLocal !== b.isLocal) {
                    return a.isLocal ? -1 : 1;
                }
                return a.name.localeCompare(b.name);
            });

            return allMethods;
        } catch (error) {
            console.error('Failed to get shader members:', error);
            return [];
        }
    }

    getParent(_element: MemberInfo): vscode.ProviderResult<MemberInfo> {
        return null;
    }
}
