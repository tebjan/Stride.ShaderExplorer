import * as vscode from 'vscode';
import type { LanguageClient } from 'vscode-languageclient/node';

export interface MemberInfo {
    name: string;
    type: string;
    signature: string;
    comment: string | null;
    line: number;
    filePath: string;
    isLocal: boolean;
}

export interface MemberGroup {
    sourceShader: string;
    filePath: string;
    members: MemberInfo[];
    isLocal: boolean;
}

export interface ShaderMembersResponse {
    streams: MemberInfo[];
    variables: MemberGroup[];
    methods: MemberGroup[];
}

type VariableTreeItem = MemberGroup | MemberInfo;

function isMemberGroup(item: VariableTreeItem): item is MemberGroup {
    return 'sourceShader' in item && 'members' in item;
}

export class VariablesTreeProvider implements vscode.TreeDataProvider<VariableTreeItem> {
    private _onDidChangeTreeData = new vscode.EventEmitter<VariableTreeItem | undefined>();
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

    getTreeItem(element: VariableTreeItem): vscode.TreeItem {
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

        // This is a member (variable)
        const item = new vscode.TreeItem(element.name, vscode.TreeItemCollapsibleState.None);

        // Type as description
        item.description = element.type;

        // Build tooltip
        item.tooltip = new vscode.MarkdownString();
        item.tooltip.appendCodeblock(`${element.type} ${element.name}`, 'sdsl');
        if (element.comment) {
            item.tooltip.appendMarkdown(`\n\n${element.comment}`);
        }

        // Icon: local vs inherited
        item.iconPath = new vscode.ThemeIcon(
            element.isLocal ? 'symbol-field' : 'symbol-constant'
        );

        item.contextValue = element.isLocal ? 'localVariable' : 'inheritedVariable';

        // Command to open file at variable location
        if (element.filePath && element.line > 0) {
            item.command = {
                command: 'strideShaderTools.openShader',
                title: 'Go to Definition',
                arguments: [element.filePath, element.line]
            };
        }

        return item;
    }

    async getChildren(element?: VariableTreeItem): Promise<VariableTreeItem[]> {
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

                // Return variable groups (sorted: local first, then inherited)
                return this.cachedData?.variables ?? [];
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

    getParent(_element: VariableTreeItem): vscode.ProviderResult<VariableTreeItem> {
        return null;
    }
}
