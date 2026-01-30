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
    sourceShader: string;
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

export class VariablesTreeProvider implements vscode.TreeDataProvider<MemberInfo> {
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
        // Format: type name
        const varLabel = `${element.type} ${element.name}`;
        const item = new vscode.TreeItem(varLabel, vscode.TreeItemCollapsibleState.None);

        // Always show source shader as description (right-aligned, gray)
        item.description = element.sourceShader;

        // Build tooltip
        item.tooltip = new vscode.MarkdownString();
        item.tooltip.appendCodeblock(`${element.type} ${element.name}`, 'sdsl');
        item.tooltip.appendMarkdown(`\n\nDefined in: **${element.sourceShader}**`);
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

    async getChildren(_element?: MemberInfo): Promise<MemberInfo[]> {
        if (!this.client) {
            console.log('[Variables] No client available');
            return [];
        }

        const activeEditor = vscode.window.activeTextEditor;
        if (!activeEditor || activeEditor.document.languageId !== 'sdsl') {
            console.log('[Variables] No active SDSL editor');
            return [];
        }

        const uri = activeEditor.document.uri.toString();

        try {
            if (this.currentUri !== uri || !this.cachedData) {
                this.currentUri = uri;
                console.log('[Variables] Requesting shader members for:', uri);
                this.cachedData = await this.client.sendRequest<ShaderMembersResponse>(
                    'stride/getShaderMembers',
                    { uri }
                );
                console.log('[Variables] Response variables:', this.cachedData?.variables?.length ?? 0);
            }

            // Flatten all variables from groups into a single list
            // Sort: local variables first, then inherited (alphabetically within each)
            const allVariables: MemberInfo[] = [];
            for (const group of this.cachedData?.variables ?? []) {
                allVariables.push(...group.members);
            }

            // Sort: local first, then by name
            allVariables.sort((a, b) => {
                if (a.isLocal !== b.isLocal) {
                    return a.isLocal ? -1 : 1;
                }
                return a.name.localeCompare(b.name);
            });

            return allVariables;
        } catch (error) {
            console.error('[Variables] Failed to get shader members:', error);
            return [];
        }
    }

    getParent(_element: MemberInfo): vscode.ProviderResult<MemberInfo> {
        return null;
    }
}
