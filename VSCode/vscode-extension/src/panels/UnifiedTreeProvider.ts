import * as vscode from 'vscode';
import type { LanguageClient } from 'vscode-languageclient/node';

// Shared interfaces for LSP communication
export interface ShaderNode {
    name: string;
    filePath: string;
    source: string;
    line: number;
    isLocal: boolean;
}

export interface MemberInfo {
    name: string;
    type: string;
    signature: string;
    comment: string | null;
    line: number;
    filePath: string;
    isLocal: boolean;
    sourceShader: string;
    isStage: boolean;
    isEntryPoint: boolean;
}

export interface MemberGroup {
    sourceShader: string;
    filePath: string;
    members: MemberInfo[];
    isLocal: boolean;
}

export interface InheritanceTreeResponse {
    currentShader: ShaderNode | null;
    baseShaders: ShaderNode[];
}

export interface ShaderMembersResponse {
    streams: MemberInfo[];
    variables: MemberGroup[];
    methods: MemberGroup[];
}

// Tree node types for the unified provider
type CategoryType = 'inheritance' | 'streams' | 'variables' | 'methods';

interface RootNode {
    type: 'root';
    shaderName: string;
    filePath: string;
}

interface CategoryNode {
    type: 'category';
    category: CategoryType;
    count: number;
}

interface ShaderMemberNode {
    type: 'shader';
    shader: ShaderNode;
}

interface MemberNode {
    type: 'member';
    member: MemberInfo;
    category: CategoryType;
}

type TreeNode = RootNode | CategoryNode | ShaderMemberNode | MemberNode;

export class UnifiedTreeProvider implements vscode.TreeDataProvider<TreeNode> {
    private _onDidChangeTreeData = new vscode.EventEmitter<TreeNode | undefined>();
    readonly onDidChangeTreeData = this._onDidChangeTreeData.event;

    private cachedInheritance: InheritanceTreeResponse | null = null;
    private cachedMembers: ShaderMembersResponse | null = null;
    private currentUri: string | null = null;

    constructor(private client: LanguageClient | undefined) {}

    setClient(client: LanguageClient) {
        this.client = client;
    }

    /**
     * Full refresh - rebuilds entire tree (use when switching files).
     */
    refresh(): void {
        this.cachedInheritance = null;
        this.cachedMembers = null;
        this._onDidChangeTreeData.fire(undefined);
    }

    /**
     * Soft refresh - invalidates cache and updates visible data.
     * Note: VS Code TreeView doesn't preserve expansion state well, so this
     * behaves similar to full refresh. The debounce in extension.ts helps
     * minimize disruption while typing.
     */
    softRefresh(): void {
        this.cachedInheritance = null;
        this.cachedMembers = null;
        this._onDidChangeTreeData.fire(undefined);
    }

    getTreeItem(element: TreeNode): vscode.TreeItem {
        switch (element.type) {
            case 'root':
                return this.createRootItem(element);
            case 'category':
                return this.createCategoryItem(element);
            case 'shader':
                return this.createShaderItem(element);
            case 'member':
                return this.createMemberItem(element);
        }
    }

    private createRootItem(element: RootNode): vscode.TreeItem {
        const item = new vscode.TreeItem(
            element.shaderName,
            vscode.TreeItemCollapsibleState.Expanded
        );
        item.iconPath = new vscode.ThemeIcon('symbol-class');
        item.description = 'current shader';
        item.contextValue = 'currentShader';
        return item;
    }

    private createCategoryItem(element: CategoryNode): vscode.TreeItem {
        const labels: Record<CategoryType, string> = {
            inheritance: 'Inheritance',
            streams: 'Streams',
            variables: 'Variables',
            methods: 'Methods'
        };
        const icons: Record<CategoryType, string> = {
            inheritance: 'type-hierarchy',
            streams: 'pulse',
            variables: 'symbol-variable',
            methods: 'symbol-method'
        };

        const label = labels[element.category];
        const countLabel = element.count > 0 ? ` (${element.count})` : '';

        const item = new vscode.TreeItem(
            label + countLabel,
            vscode.TreeItemCollapsibleState.Collapsed
        );
        item.iconPath = new vscode.ThemeIcon(icons[element.category]);
        item.contextValue = `category-${element.category}`;
        return item;
    }

    private createShaderItem(element: ShaderMemberNode): vscode.TreeItem {
        const shader = element.shader;
        const item = new vscode.TreeItem(
            shader.name,
            vscode.TreeItemCollapsibleState.None
        );
        item.description = shader.source;
        item.iconPath = new vscode.ThemeIcon('symbol-interface');
        item.tooltip = new vscode.MarkdownString();
        item.tooltip.appendMarkdown(`**${shader.name}**\n\n`);
        item.tooltip.appendMarkdown(`Source: ${shader.source}\n\n`);
        item.tooltip.appendMarkdown(`\`${shader.filePath}\``);
        item.command = {
            command: 'strideShaderTools.openShader',
            title: 'Open Shader',
            arguments: [shader.filePath, shader.line]
        };
        item.contextValue = 'baseShader';
        return item;
    }

    private createMemberItem(element: MemberNode): vscode.TreeItem {
        const member = element.member;

        // Build the label with stage prefix
        const stagePrefix = member.isStage ? 'stage ' : '';
        let label: string;

        if (element.category === 'methods') {
            label = `${stagePrefix}${member.type} ${member.name}${member.signature || '()'}`;
        } else if (element.category === 'streams') {
            label = `${stagePrefix}${member.type} ${member.name}`;
        } else {
            label = `${stagePrefix}${member.type} ${member.name}`;
        }

        const item = new vscode.TreeItem(label, vscode.TreeItemCollapsibleState.None);

        // Description: source shader, plus stage name for entry points
        if (member.isEntryPoint) {
            const stageName = this.getShaderStageName(member.name);
            item.description = `${member.sourceShader} • ${stageName}`;
        } else {
            item.description = member.sourceShader;
        }

        // Icon based on local vs inherited, special icon for entry points
        if (member.isEntryPoint) {
            // Use play icon for entry points
            item.iconPath = new vscode.ThemeIcon('play', new vscode.ThemeColor('charts.green'));
        } else {
            const iconMap: Record<CategoryType, [string, string]> = {
                streams: ['symbol-property', 'symbol-constant'],
                variables: ['symbol-field', 'symbol-constant'],
                methods: ['symbol-method', 'symbol-function'],
                inheritance: ['symbol-class', 'symbol-interface']
            };
            const [localIcon, inheritedIcon] = iconMap[element.category] || ['symbol-field', 'symbol-constant'];
            item.iconPath = new vscode.ThemeIcon(member.isLocal ? localIcon : inheritedIcon);
        }

        // Build tooltip
        item.tooltip = new vscode.MarkdownString();
        const qualifiers = member.isStage ? 'stage ' : '';
        if (element.category === 'methods') {
            item.tooltip.appendCodeblock(`${qualifiers}${member.type} ${member.name}${member.signature}`, 'sdsl');
            if (member.isEntryPoint) {
                const stageName = this.getShaderStageName(member.name);
                item.tooltip.appendMarkdown(`\n\n**Shader Stage Entry Point** (${stageName})`);
            }
        } else if (element.category === 'streams') {
            item.tooltip.appendCodeblock(`stream ${qualifiers}${member.type} ${member.name}`, 'sdsl');
        } else {
            item.tooltip.appendCodeblock(`${qualifiers}${member.type} ${member.name}`, 'sdsl');
        }
        item.tooltip.appendMarkdown(`\n\nDefined in: **${member.sourceShader}**`);
        if (member.comment) {
            item.tooltip.appendMarkdown(`\n\n${member.comment}`);
        }

        item.contextValue = member.isLocal ? `local${element.category}` : `inherited${element.category}`;

        // Command to navigate to definition
        if (member.filePath && member.line > 0) {
            item.command = {
                command: 'strideShaderTools.openShader',
                title: 'Go to Definition',
                arguments: [member.filePath, member.line]
            };
        }

        return item;
    }

    /**
     * Get the human-readable shader stage name for an entry point method.
     */
    private getShaderStageName(methodName: string): string {
        const stages: Record<string, string> = {
            'VSMain': 'Vertex',
            'HSMain': 'Hull',
            'HSConstantMain': 'Hull Constant',
            'DSMain': 'Domain',
            'GSMain': 'Geometry',
            'PSMain': 'Pixel',
            'CSMain': 'Compute',
            'ShadeVertex': 'Vertex',
            'ShadePixel': 'Pixel',
        };
        return stages[methodName] || 'Unknown';
    }

    async getChildren(element?: TreeNode): Promise<TreeNode[]> {
        if (!this.client) {
            return [];
        }

        const activeEditor = vscode.window.activeTextEditor;
        if (!activeEditor || activeEditor.document.languageId !== 'sdsl') {
            return [];
        }

        const uri = activeEditor.document.uri.toString();

        // Root level: show current shader with category children
        if (!element) {
            await this.ensureDataLoaded(uri);

            const currentShader = this.cachedInheritance?.currentShader;
            if (!currentShader) {
                return [];
            }

            return [{
                type: 'root',
                shaderName: currentShader.name,
                filePath: currentShader.filePath
            }];
        }

        // Root node: show categories
        if (element.type === 'root') {
            await this.ensureDataLoaded(uri);

            const inheritanceCount = this.cachedInheritance?.baseShaders?.length ?? 0;
            const streamsCount = this.cachedMembers?.streams?.length ?? 0;
            const variablesCount = this.countMembers(this.cachedMembers?.variables ?? []);
            const methodsCount = this.countMembers(this.cachedMembers?.methods ?? []);

            return [
                { type: 'category', category: 'inheritance', count: inheritanceCount },
                { type: 'category', category: 'streams', count: streamsCount },
                { type: 'category', category: 'variables', count: variablesCount },
                { type: 'category', category: 'methods', count: methodsCount }
            ];
        }

        // Category node: show items
        if (element.type === 'category') {
            await this.ensureDataLoaded(uri);
            return this.getCategoryChildren(element.category);
        }

        return [];
    }

    private async ensureDataLoaded(uri: string): Promise<void> {
        if (this.currentUri === uri && this.cachedInheritance && this.cachedMembers) {
            return;
        }

        this.currentUri = uri;

        try {
            // Load both in parallel
            const [inheritanceRaw, membersRaw] = await Promise.all([
                this.client!.sendRequest<InheritanceTreeResponse>('stride/getInheritanceTree', { uri }),
                this.client!.sendRequest<ShaderMembersResponse>('stride/getShaderMembers', { uri })
            ]);

            this.cachedInheritance = inheritanceRaw;
            this.cachedMembers = membersRaw;
        } catch (error) {
            console.error('[UnifiedTree] Failed to load data:', error);
            this.cachedInheritance = null;
            this.cachedMembers = null;
        }
    }

    private getCategoryChildren(category: CategoryType): TreeNode[] {
        switch (category) {
            case 'inheritance':
                return (this.cachedInheritance?.baseShaders ?? []).map(shader => ({
                    type: 'shader' as const,
                    shader
                }));

            case 'streams':
                return this.getSortedMembers(this.cachedMembers?.streams ?? [], 'streams');

            case 'variables':
                return this.getFlattenedMembers(this.cachedMembers?.variables ?? [], 'variables');

            case 'methods':
                return this.getFlattenedMembers(this.cachedMembers?.methods ?? [], 'methods');
        }
    }

    private getSortedMembers(members: MemberInfo[], category: CategoryType): MemberNode[] {
        // Sort: local first, then by name
        const sorted = [...members].sort((a, b) => {
            if (a.isLocal !== b.isLocal) {
                return a.isLocal ? -1 : 1;
            }
            return a.name.localeCompare(b.name);
        });

        return sorted.map(member => ({
            type: 'member' as const,
            member,
            category
        }));
    }

    private getFlattenedMembers(groups: MemberGroup[], category: CategoryType): MemberNode[] {
        const allMembers: MemberInfo[] = [];
        for (const group of groups) {
            allMembers.push(...group.members);
        }

        return this.getSortedMembers(allMembers, category);
    }

    private countMembers(groups: MemberGroup[]): number {
        return groups.reduce((sum, g) => sum + g.members.length, 0);
    }

    getParent(_element: TreeNode): vscode.ProviderResult<TreeNode> {
        return null;
    }
}
