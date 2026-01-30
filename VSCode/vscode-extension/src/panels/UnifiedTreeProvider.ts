import * as vscode from 'vscode';
import type { LanguageClient } from 'vscode-languageclient/node';

// Shared interfaces for LSP communication
export interface ShaderNode {
    name: string;
    filePath: string;
    source: string;
    line: number;
    isLocal: boolean;
    children?: ShaderNode[] | null;  // Direct base shaders for hierarchical display
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

export interface CompositionInfo {
    name: string;
    type: string;
    line: number;
    filePath: string;
    isLocal: boolean;
    sourceShader: string;
}

export interface InheritanceTreeResponse {
    currentShader: ShaderNode | null;
    baseShaders: ShaderNode[];
}

export interface ShaderMembersResponse {
    streams: MemberInfo[];
    variables: MemberGroup[];
    methods: MemberGroup[];
    compositions: CompositionInfo[];
}

// Tree node types for the unified provider
type CategoryType = 'inheritance' | 'compositions' | 'streams' | 'variables' | 'methods';

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

interface CompositionNode {
    type: 'composition';
    composition: CompositionInfo;
}

type TreeNode = RootNode | CategoryNode | ShaderMemberNode | MemberNode | CompositionNode;

export class UnifiedTreeProvider implements vscode.TreeDataProvider<TreeNode> {
    private _onDidChangeTreeData = new vscode.EventEmitter<TreeNode | undefined>();
    readonly onDidChangeTreeData = this._onDidChangeTreeData.event;

    private cachedInheritance: InheritanceTreeResponse | null = null;
    private cachedMembers: ShaderMembersResponse | null = null;
    private currentUri: string | null = null;

    // Track collapsed nodes per file URI (nodes not in set are expanded)
    private collapsedNodesPerFile: Map<string, Set<string>> = new Map();

    constructor(private client: LanguageClient | undefined) {}

    /**
     * Called by extension.ts when tree view expansion state changes.
     * @param nodeId The unique ID of the node
     * @param collapsed Whether the node was collapsed (true) or expanded (false)
     */
    onNodeExpansionChanged(nodeId: string, collapsed: boolean): void {
        if (!this.currentUri) return;

        let collapsedNodes = this.collapsedNodesPerFile.get(this.currentUri);
        if (!collapsedNodes) {
            collapsedNodes = new Set();
            this.collapsedNodesPerFile.set(this.currentUri, collapsedNodes);
        }

        if (collapsed) {
            collapsedNodes.add(nodeId);
        } else {
            collapsedNodes.delete(nodeId);
        }
    }

    /**
     * Get the stored expansion state for a node.
     */
    private isNodeCollapsed(nodeId: string): boolean {
        if (!this.currentUri) return false;
        const collapsedNodes = this.collapsedNodesPerFile.get(this.currentUri);
        return collapsedNodes?.has(nodeId) ?? false;
    }

    /**
     * Generate a unique ID for a tree node.
     */
    private getNodeId(element: TreeNode): string {
        switch (element.type) {
            case 'root':
                return `root:${element.shaderName}`;
            case 'category':
                return `category:${element.category}`;
            case 'shader':
                return `shader:${element.shader.name}`;
            case 'member':
                return `member:${element.category}:${element.member.sourceShader}:${element.member.name}`;
            case 'composition':
                return `composition:${element.composition.sourceShader}:${element.composition.name}`;
        }
    }

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
            case 'composition':
                return this.createCompositionItem(element);
        }
    }

    private createRootItem(element: RootNode): vscode.TreeItem {
        const nodeId = this.getNodeId(element);
        const isCollapsed = this.isNodeCollapsed(nodeId);

        const item = new vscode.TreeItem(
            element.shaderName,
            isCollapsed ? vscode.TreeItemCollapsibleState.Collapsed : vscode.TreeItemCollapsibleState.Expanded
        );
        item.id = nodeId;
        item.iconPath = new vscode.ThemeIcon('symbol-class');
        item.description = 'current shader';
        item.contextValue = 'currentShader';
        return item;
    }

    private createCategoryItem(element: CategoryNode): vscode.TreeItem {
        const labels: Record<CategoryType, string> = {
            inheritance: 'Inheritance',
            compositions: 'Compositions',
            streams: 'Streams',
            variables: 'Variables',
            methods: 'Methods'
        };
        const icons: Record<CategoryType, string> = {
            inheritance: 'type-hierarchy',
            compositions: 'extensions',
            streams: 'pulse',
            variables: 'symbol-variable',
            methods: 'symbol-method'
        };

        const nodeId = this.getNodeId(element);
        const label = labels[element.category];
        const countLabel = element.count > 0 ? ` (${element.count})` : '';

        // Check stored state, with defaults for Inheritance and Methods expanded
        const expandedByDefault = element.category === 'inheritance' || element.category === 'methods';
        const isCollapsed = this.isNodeCollapsed(nodeId);
        const collapsibleState = isCollapsed
            ? vscode.TreeItemCollapsibleState.Collapsed
            : (expandedByDefault ? vscode.TreeItemCollapsibleState.Expanded : vscode.TreeItemCollapsibleState.Collapsed);

        const item = new vscode.TreeItem(label + countLabel, collapsibleState);
        item.id = nodeId;
        item.iconPath = new vscode.ThemeIcon(icons[element.category]);
        item.contextValue = `category-${element.category}`;
        return item;
    }

    /**
     * Format the source display for the inheritance tree.
     * For Stride shaders: just show the subfolder path (e.g., "Core/Shaders")
     * For vvvv shaders: show subfolder path + origin suffix (e.g., "Assets/Effects (vvvv@7.1-144)")
     * For workspace shaders: just show the relative path
     */
    private formatSource(source: string): string {
        // Check if it's a Stride package (starts with "stride.")
        const strideMatch = source.match(/^stride\.[^@]+@[\d.]+\/(.+)/i);
        if (strideMatch) {
            // For Stride: just return the subfolder path
            return strideMatch[1];
        }

        // Check if it's vvvv (starts with "vvvv@")
        const vvvvMatch = source.match(/^(vvvv@[\d.-]+)\/(.+)/i);
        if (vvvvMatch) {
            // For vvvv: show subfolder path + origin in parentheses
            const origin = vvvvMatch[1];
            const subpath = vvvvMatch[2];
            return `${subpath} (${origin})`;
        }

        // Workspace or unknown: return as-is (already relative to workspace)
        return source;
    }

    private createShaderItem(element: ShaderMemberNode): vscode.TreeItem {
        const shader = element.shader;
        const formattedSource = this.formatSource(shader.source);
        const nodeId = this.getNodeId(element);

        // Determine if this shader has children (its own base shaders)
        const hasChildren = shader.children && shader.children.length > 0;
        let collapsibleState: vscode.TreeItemCollapsibleState;
        if (!hasChildren) {
            collapsibleState = vscode.TreeItemCollapsibleState.None;
        } else {
            const isCollapsed = this.isNodeCollapsed(nodeId);
            collapsibleState = isCollapsed
                ? vscode.TreeItemCollapsibleState.Collapsed
                : vscode.TreeItemCollapsibleState.Expanded;
        }

        const item = new vscode.TreeItem(shader.name, collapsibleState);
        item.id = nodeId;
        item.description = formattedSource;
        item.iconPath = new vscode.ThemeIcon('symbol-interface');
        item.tooltip = new vscode.MarkdownString();
        item.tooltip.appendMarkdown(`**${shader.name}**\n\n`);
        item.tooltip.appendMarkdown(`Source: ${formattedSource}\n\n`);
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
                inheritance: ['symbol-class', 'symbol-interface'],
                compositions: ['extensions', 'package']
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

    private createCompositionItem(element: CompositionNode): vscode.TreeItem {
        const comp = element.composition;
        const label = `${comp.type} ${comp.name}`;

        const item = new vscode.TreeItem(label, vscode.TreeItemCollapsibleState.None);
        item.description = comp.sourceShader;
        item.iconPath = new vscode.ThemeIcon(comp.isLocal ? 'extensions' : 'package');

        item.tooltip = new vscode.MarkdownString();
        item.tooltip.appendCodeblock(`compose ${comp.type} ${comp.name}`, 'sdsl');
        item.tooltip.appendMarkdown(`\n\nDefined in: **${comp.sourceShader}**`);

        item.contextValue = comp.isLocal ? 'localComposition' : 'inheritedComposition';

        // Command to navigate to definition
        if (comp.filePath && comp.line > 0) {
            item.command = {
                command: 'strideShaderTools.openShader',
                title: 'Go to Definition',
                arguments: [comp.filePath, comp.line]
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
            const compositionsCount = this.cachedMembers?.compositions?.length ?? 0;
            const streamsCount = this.cachedMembers?.streams?.length ?? 0;
            const variablesCount = this.countMembers(this.cachedMembers?.variables ?? []);
            const methodsCount = this.countMembers(this.cachedMembers?.methods ?? []);

            const categories: TreeNode[] = [
                { type: 'category', category: 'inheritance', count: inheritanceCount },
            ];

            // Only show compositions category if there are any
            if (compositionsCount > 0) {
                categories.push({ type: 'category', category: 'compositions', count: compositionsCount });
            }

            categories.push(
                { type: 'category', category: 'streams', count: streamsCount },
                { type: 'category', category: 'variables', count: variablesCount },
                { type: 'category', category: 'methods', count: methodsCount }
            );

            return categories;
        }

        // Category node: show items
        if (element.type === 'category') {
            await this.ensureDataLoaded(uri);
            return this.getCategoryChildren(element.category);
        }

        // Shader node: show its base shaders (children) for hierarchical inheritance
        if (element.type === 'shader') {
            const shader = element.shader;
            if (shader.children && shader.children.length > 0) {
                return shader.children.map(child => ({
                    type: 'shader' as const,
                    shader: child
                }));
            }
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
                // Use hierarchical children from currentShader for proper tree structure
                const directBases = this.cachedInheritance?.currentShader?.children ?? [];
                return directBases.map(shader => ({
                    type: 'shader' as const,
                    shader
                }));

            case 'compositions':
                return this.getSortedCompositions(this.cachedMembers?.compositions ?? []);

            case 'streams':
                return this.getSortedMembers(this.cachedMembers?.streams ?? [], 'streams');

            case 'variables':
                return this.getFlattenedMembers(this.cachedMembers?.variables ?? [], 'variables');

            case 'methods':
                return this.getFlattenedMembers(this.cachedMembers?.methods ?? [], 'methods');
        }
    }

    private getSortedCompositions(compositions: CompositionInfo[]): CompositionNode[] {
        // Sort: local first, then by name
        const sorted = [...compositions].sort((a, b) => {
            if (a.isLocal !== b.isLocal) {
                return a.isLocal ? -1 : 1;
            }
            return a.name.localeCompare(b.name);
        });

        return sorted.map(composition => ({
            type: 'composition' as const,
            composition
        }));
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
