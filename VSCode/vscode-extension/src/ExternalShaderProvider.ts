import * as fs from 'node:fs';
import * as vscode from 'vscode';

/**
 * URI scheme for external (read-only) shaders.
 * Format: sdsl-external:/path/to/shader.sdsl
 */
export const EXTERNAL_SHADER_SCHEME = 'sdsl-external';

/**
 * FileSystemProvider that serves external shader files as read-only virtual documents.
 * This allows users to view Stride/vvvv shader source without being able to edit it.
 */
export class ExternalShaderProvider implements vscode.FileSystemProvider {
    private _onDidChangeFile = new vscode.EventEmitter<vscode.FileChangeEvent[]>();
    readonly onDidChangeFile = this._onDidChangeFile.event;

    /**
     * Convert URI path to a real file system path.
     * On Windows, uri.path returns "/C:/path/..." but we need "C:/path/...".
     */
    private getRealPath(uri: vscode.Uri): string {
        // Use fsPath which handles Windows paths correctly
        // uri.fsPath converts "/C:/path" to "c:\path" on Windows
        return uri.fsPath;
    }

    watch(): vscode.Disposable {
        // No need to watch external files
        return new vscode.Disposable(() => {});
    }

    stat(uri: vscode.Uri): vscode.FileStat {
        const realPath = this.getRealPath(uri);
        try {
            const stats = fs.statSync(realPath);
            return {
                type: vscode.FileType.File,
                ctime: stats.ctimeMs,
                mtime: stats.mtimeMs,
                size: stats.size,
            };
        } catch {
            throw vscode.FileSystemError.FileNotFound(uri);
        }
    }

    readDirectory(): [string, vscode.FileType][] {
        return [];
    }

    createDirectory(): void {
        throw vscode.FileSystemError.NoPermissions('External shaders are read-only');
    }

    readFile(uri: vscode.Uri): Uint8Array {
        const realPath = this.getRealPath(uri);
        try {
            const content = fs.readFileSync(realPath);
            return content;
        } catch {
            throw vscode.FileSystemError.FileNotFound(uri);
        }
    }

    writeFile(): void {
        throw vscode.FileSystemError.NoPermissions('External shaders are read-only');
    }

    delete(): void {
        throw vscode.FileSystemError.NoPermissions('External shaders are read-only');
    }

    rename(): void {
        throw vscode.FileSystemError.NoPermissions('External shaders are read-only');
    }
}

/**
 * Create a read-only URI for an external shader.
 *
 * @param filePath - The real file path to the shader
 * @param lineNumber - Optional line number to navigate to (1-based)
 * @returns URI with sdsl-external scheme
 */
export function createExternalShaderUri(filePath: string, lineNumber?: number): vscode.Uri {
    // First create a file URI to properly handle Windows paths,
    // then change the scheme to our custom scheme.
    // This ensures proper path encoding (C:\path -> /c:/path)
    const fileUri = vscode.Uri.file(filePath);
    let uri = fileUri.with({ scheme: EXTERNAL_SHADER_SCHEME });

    if (lineNumber !== undefined && lineNumber > 0) {
        uri = uri.with({ fragment: `L${lineNumber}` });
    }

    return uri;
}

/**
 * Check if a URI is for an external (read-only) shader.
 */
export function isExternalShaderUri(uri: vscode.Uri): boolean {
    return uri.scheme === EXTERNAL_SHADER_SCHEME;
}
