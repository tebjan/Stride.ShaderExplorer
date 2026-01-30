import * as vscode from 'vscode';
import type { LanguageClient } from 'vscode-languageclient/node';
import type { MemberGroup, MemberInfo, ShaderMembersResponse } from './VariablesTreeProvider';

type MethodTreeItem = MemberGroup | MemberInfo;

function isMemberGroup(item: MethodTreeItem): item is MemberGroup {
    return 'sourceShader' in item && 'members' in item;
}

export class MethodsTreeProvider implements vscode.TreeDataProvider<MethodTreeItem> {
    private _onDidChangeTreeData = new vscode.EventEmitter<MethodTreeItem | undefined>();
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

    getTreeItem(element: MethodTreeItem): vscode.TreeItem {
        if (isMemberGroup(element)) {
            // This is a group header (shader name)
            const item = new vscode.TreeItem(
                element.sourceShader,
                vscode.TreeItemCollapsibleState.Expanded
            );

            // Show count in description
            item.description = `(${element.members.length})`;

            // Icon based on whether it's local or inherited
            item.iconPath = new vscode.ThemeIcon(
                element.isLocal ? 'symbol-class' : 'symbol-interface'
            );

            item.contextValue = 'memberGroup';

            return item;
        }

        // This is a method
        const item = new vscode.TreeItem(element.name, vscode.TreeItemCollapsibleState.None);

        // Signature as description (return type)
        item.description = element.signature || element.type;

        // Build tooltip
        item.tooltip = new vscode.MarkdownString();
        const fullSignature = element.signature
            ? `${element.type} ${element.name}${element.signature}`
            : `${element.type} ${element.name}()`;
        item.tooltip.appendCodeblock(fullSignature, 'sdsl');
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

    async getChildren(element?: MethodTreeItem): Promise<MethodTreeItem[]> {
        if (!this.client) {
            return [];
        }

        const activeEditor = vscode.window.activeTextEditor;
        if (!activeEditor || activeEditor.document.languageId !== 'sdsl') {
            return [];
        }

        const uri = activeEditor.document.uri.toString();

        // Root level: return groups
        if (!element) {
            try {
                if (this.currentUri !== uri || !this.cachedData) {
                    this.currentUri = uri;
                    this.cachedData = await this.client.sendRequest<ShaderMembersResponse>(
                        'stride/getShaderMembers',
                        { uri }
                    );
                }

                // Return method groups (sorted: local first, then inherited)
                return this.cachedData?.methods ?? [];
            } catch (error) {
                console.error('Failed to get shader members:', error);
                return [];
            }
        }

        // Group level: return members
        if (isMemberGroup(element)) {
            return element.members;
        }

        return [];
    }

    getParent(_element: MethodTreeItem): vscode.ProviderResult<MethodTreeItem> {
        return null;
    }
}
