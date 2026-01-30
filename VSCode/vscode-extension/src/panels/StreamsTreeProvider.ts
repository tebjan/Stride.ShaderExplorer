import * as vscode from 'vscode';
import type { LanguageClient } from 'vscode-languageclient/node';
import type { MemberInfo, ShaderMembersResponse } from './VariablesTreeProvider';

export class StreamsTreeProvider implements vscode.TreeDataProvider<MemberInfo> {
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
        const item = new vscode.TreeItem(element.name, vscode.TreeItemCollapsibleState.None);

        // Type as description
        item.description = element.type;

        // Build tooltip with source shader info
        item.tooltip = new vscode.MarkdownString();
        item.tooltip.appendCodeblock(`stream ${element.type} ${element.name}`, 'sdsl');
        if (element.comment) {
            item.tooltip.appendMarkdown(`\n\n${element.comment}`);
        }
        if (element.filePath) {
            item.tooltip.appendMarkdown(`\n\n*Defined in: ${element.filePath}*`);
        }

        // Stream icon
        item.iconPath = new vscode.ThemeIcon('symbol-property');

        item.contextValue = 'stream';

        // Command to open file at stream location
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

            // Return flat list of streams
            return this.cachedData?.streams ?? [];
        } catch (error) {
            console.error('Failed to get shader streams:', error);
            return [];
        }
    }

    getParent(_element: MemberInfo): vscode.ProviderResult<MemberInfo> {
        return null;
    }
}
