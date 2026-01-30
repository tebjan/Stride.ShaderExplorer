using Microsoft.Extensions.Logging;

namespace StrideShaderLanguageServer.Services;

/// <summary>
/// Manages the shader workspace - discovers, indexes, and provides access to all shaders.
/// </summary>
public class ShaderWorkspace
{
    private readonly ILogger<ShaderWorkspace> _logger;
    private readonly ShaderParser _parser;
    private readonly List<string> _workspaceFolders = new();
    private readonly List<(string Path, ShaderSource Source)> _shaderSearchPaths = new();
    private readonly Dictionary<string, ShaderInfo> _shadersByName = new();
    private readonly Dictionary<string, ShaderInfo> _shadersByPath = new();
    private readonly Dictionary<string, ParsedShader> _lastValidParse = new();
    private readonly List<PathDisplayRule> _pathDisplayRules = new();
    private readonly object _lock = new();

    public event EventHandler? IndexingComplete;

    public ShaderWorkspace(ILogger<ShaderWorkspace> logger, ShaderParser parser)
    {
        _logger = logger;
        _parser = parser;
    }

    public void AddWorkspaceFolder(string path)
    {
        lock (_lock)
        {
            if (!_workspaceFolders.Contains(path))
            {
                _workspaceFolders.Add(path);
                _logger.LogInformation("Added workspace folder: {Path}", path);
            }
        }
    }

    public void DiscoverShaderPaths()
    {
        _logger.LogInformation("Discovering shader paths...");

        // 1. NuGet packages cache (Stride core)
        var nugetPath = GetNuGetPackagesPath();
        _logger.LogInformation("NuGet packages path: {Path}", nugetPath ?? "NOT FOUND");
        if (nugetPath != null && Directory.Exists(nugetPath))
        {
            var stridePaths = DiscoverStrideNuGetPackages(nugetPath);
            _logger.LogInformation("Found {Count} Stride NuGet package paths", stridePaths.Count);
            foreach (var path in stridePaths)
            {
                AddShaderSearchPath(path, ShaderSource.Stride);
            }
        }
        else
        {
            _logger.LogWarning("NuGet packages path not found or doesn't exist");
        }

        // 2. vvvv gamma installations
        var vvvvPaths = DiscoverVvvvPaths();
        _logger.LogInformation("Found {Count} vvvv paths", vvvvPaths.Count);
        foreach (var path in vvvvPaths)
        {
            AddShaderSearchPath(path, ShaderSource.Vvvv);
        }

        // 3. Workspace folders (user's local shaders)
        _logger.LogInformation("Adding {Count} workspace folders", _workspaceFolders.Count);
        foreach (var folder in _workspaceFolders)
        {
            AddShaderSearchPath(folder, ShaderSource.Workspace);
        }

        _logger.LogInformation("Discovered {Count} total shader search paths", _shaderSearchPaths.Count);
    }

    private void AddShaderSearchPath(string path, ShaderSource source)
    {
        lock (_lock)
        {
            if (!_shaderSearchPaths.Any(p => p.Path == path))
            {
                _shaderSearchPaths.Add((path, source));
                _logger.LogInformation("Added shader search path: {Path} (source: {Source})", path, source);
            }
        }
    }

    public void IndexAllShaders()
    {
        _logger.LogInformation("Indexing all shaders...");

        var shaderFiles = new List<(string FilePath, ShaderSource Source)>();

        lock (_lock)
        {
            foreach (var (searchPath, source) in _shaderSearchPaths)
            {
                try
                {
                    if (Directory.Exists(searchPath))
                    {
                        var files = Directory.EnumerateFiles(searchPath, "*.sdsl", SearchOption.AllDirectories);
                        foreach (var file in files)
                        {
                            shaderFiles.Add((file, source));
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error searching for shaders in {Path}", searchPath);
                }
            }
        }

        _logger.LogInformation("Found {Count} shader files", shaderFiles.Count);

        foreach (var (file, source) in shaderFiles)
        {
            try
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var displayPath = GetDisplayPath(file);
                var info = new ShaderInfo(name, file, displayPath, source);

                lock (_lock)
                {
                    _shadersByName[name] = info;
                    _shadersByPath[file] = info;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error indexing shader file {Path}", file);
            }
        }

        _logger.LogInformation("Indexed {Count} shaders", _shadersByName.Count);

        IndexingComplete?.Invoke(this, EventArgs.Empty);
    }

    public ShaderInfo? GetShaderByName(string name)
    {
        lock (_lock)
        {
            return _shadersByName.TryGetValue(name, out var info) ? info : null;
        }
    }

    public ShaderInfo? GetShaderByPath(string path)
    {
        lock (_lock)
        {
            return _shadersByPath.TryGetValue(path, out var info) ? info : null;
        }
    }

    public IReadOnlyList<ShaderInfo> GetAllShaders()
    {
        lock (_lock)
        {
            return _shadersByName.Values.ToList();
        }
    }

    public IReadOnlyList<string> GetAllShaderNames()
    {
        lock (_lock)
        {
            return _shadersByName.Keys.ToList();
        }
    }

    public ParsedShader? GetParsedShader(string nameOrPath)
    {
        ShaderInfo? info;
        lock (_lock)
        {
            if (!_shadersByName.TryGetValue(nameOrPath, out info))
            {
                _shadersByPath.TryGetValue(nameOrPath, out info);
            }
        }

        if (info == null) return null;

        // Lazy parse
        if (info.Parsed == null)
        {
            try
            {
                var sourceCode = File.ReadAllText(info.FilePath);
                info.Parsed = _parser.TryParse(info.Name, sourceCode);

                // Cache successful parse
                if (info.Parsed != null && !info.Parsed.IsPartial)
                {
                    lock (_lock)
                    {
                        _lastValidParse[info.Name] = info.Parsed;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error reading shader file {Path}", info.FilePath);
            }
        }

        // If we have a parsed result, return it
        if (info.Parsed != null)
            return info.Parsed;

        // Fall back to last valid parse
        lock (_lock)
        {
            if (_lastValidParse.TryGetValue(info.Name, out var cached))
            {
                _logger.LogDebug("Using cached parse for {Shader} (current has errors)", info.Name);
                return cached;
            }
        }

        return null;
    }

    /// <summary>
    /// Update document and return parse result with diagnostics.
    /// </summary>
    public ShaderParseResult UpdateDocumentWithDiagnostics(string path, string content)
    {
        var name = Path.GetFileNameWithoutExtension(path);

        lock (_lock)
        {
            if (!_shadersByPath.TryGetValue(path, out var info))
            {
                var displayPath = GetDisplayPath(path);
                info = new ShaderInfo(name, path, displayPath);
                _shadersByName[name] = info;
                _shadersByPath[path] = info;
            }

            // Re-parse with new content
            _parser.InvalidateCache(name);
            var result = _parser.TryParseWithDiagnostics(name, content);

            // Update shader info
            info.Parsed = result.Shader;

            // Cache successful non-partial parses
            if (result.Shader != null && !result.IsPartial)
            {
                _lastValidParse[name] = result.Shader;
            }
            // If parse failed but we have a partial result, still use it
            else if (result.Shader == null && _lastValidParse.TryGetValue(name, out var cached))
            {
                // Keep the last valid parse available but don't overwrite info.Parsed
                // so diagnostics still show current errors
                _logger.LogDebug("Parse failed for {Shader}, last valid parse still available", name);
            }

            return result;
        }
    }

    /// <summary>
    /// Legacy method - updates document without returning diagnostics.
    /// </summary>
    public void UpdateDocument(string path, string content)
    {
        UpdateDocumentWithDiagnostics(path, content);
    }

    #region Path Discovery

    private static string? GetNuGetPackagesPath()
    {
        // Check environment variable first
        var envPath = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (!string.IsNullOrEmpty(envPath) && Directory.Exists(envPath))
            return envPath;

        // Default locations
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var defaultPath = Path.Combine(userProfile, ".nuget", "packages");
        if (Directory.Exists(defaultPath))
            return defaultPath;

        return null;
    }

    private List<string> DiscoverStrideNuGetPackages(string nugetPath)
    {
        var paths = new List<string>();

        try
        {
            var stridePackages = Directory.GetDirectories(nugetPath)
                .Where(d => Path.GetFileName(d).StartsWith("stride.", StringComparison.OrdinalIgnoreCase))
                .ToList();

            _logger.LogInformation("Found {Count} Stride.* packages in NuGet cache", stridePackages.Count);

            foreach (var package in stridePackages)
            {
                var packageName = Path.GetFileName(package);

                // Get the latest version
                var versionDir = Directory.GetDirectories(package)
                    .OrderByDescending(d => d)
                    .FirstOrDefault();

                if (versionDir != null)
                {
                    var version = Path.GetFileName(versionDir);
                    var displayPrefix = $"{packageName}@{version}";

                    // Stride shaders are in stride\Assets\ with various subdirectories
                    // (ComputeEffect, Core, Materials, Lights, Shaders, etc.)
                    // So we add the Assets folder itself and search recursively
                    var assetsPath = Path.Combine(versionDir, "stride", "Assets");
                    if (Directory.Exists(assetsPath))
                    {
                        _logger.LogInformation("Found Assets at: {Path}", assetsPath);
                        paths.Add(assetsPath);
                        AddPathDisplayRule(assetsPath, displayPrefix);
                    }

                    // Also check contentFiles locations
                    assetsPath = Path.Combine(versionDir, "contentFiles", "any", "net8.0", "stride", "Assets");
                    if (Directory.Exists(assetsPath))
                    {
                        _logger.LogInformation("Found Assets at: {Path}", assetsPath);
                        paths.Add(assetsPath);
                        AddPathDisplayRule(assetsPath, displayPrefix);
                    }

                    assetsPath = Path.Combine(versionDir, "contentFiles", "any", "net6.0", "stride", "Assets");
                    if (Directory.Exists(assetsPath))
                    {
                        _logger.LogInformation("Found Assets at: {Path}", assetsPath);
                        paths.Add(assetsPath);
                        AddPathDisplayRule(assetsPath, displayPrefix);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error discovering Stride NuGet packages");
        }

        return paths;
    }

    private void AddPathDisplayRule(string fullPath, string displayPrefix)
    {
        lock (_lock)
        {
            _pathDisplayRules.Add(new PathDisplayRule(fullPath, displayPrefix));
            _logger.LogDebug("Added display rule: {FullPath} -> {DisplayPrefix}", fullPath, displayPrefix);
        }
    }

    /// <summary>
    /// Convert a full file path to a user-friendly display path.
    /// </summary>
    public string GetDisplayPath(string fullPath)
    {
        lock (_lock)
        {
            // Try to match against known rules (longest match wins)
            var matchingRule = _pathDisplayRules
                .Where(r => fullPath.StartsWith(r.FullPathPrefix, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(r => r.FullPathPrefix.Length)
                .FirstOrDefault();

            if (matchingRule != null)
            {
                var relativePart = fullPath.Substring(matchingRule.FullPathPrefix.Length).TrimStart('\\', '/');
                return $"{matchingRule.DisplayPrefix}/{relativePart.Replace('\\', '/')}";
            }

            // For workspace files, show relative to workspace
            foreach (var workspace in _workspaceFolders)
            {
                if (fullPath.StartsWith(workspace, StringComparison.OrdinalIgnoreCase))
                {
                    return fullPath.Substring(workspace.Length).TrimStart('\\', '/').Replace('\\', '/');
                }
            }

            // Fallback: just show filename
            return Path.GetFileName(fullPath);
        }
    }

    private List<string> DiscoverVvvvPaths()
    {
        var paths = new List<string>();

        var vvvvBaseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "vvvv");

        _logger.LogInformation("Looking for vvvv at: {Path}", vvvvBaseDir);

        if (!Directory.Exists(vvvvBaseDir))
        {
            _logger.LogInformation("vvvv base directory not found");
            return paths;
        }

        try
        {
            var latestGammaDir = Directory.GetDirectories(vvvvBaseDir)
                .Where(d => Path.GetFileName(d).StartsWith("vvvv_gamma_", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(d => d)
                .FirstOrDefault();

            if (latestGammaDir == null)
            {
                _logger.LogInformation("No vvvv_gamma_* installation found");
                return paths;
            }

            var vvvvVersion = Path.GetFileName(latestGammaDir); // e.g., "vvvv_gamma_6.8"
            _logger.LogInformation("Using vvvv installation: {Path}", latestGammaDir);

            // Check packs directory
            var packsDir = Path.Combine(latestGammaDir, "packs");
            if (!Directory.Exists(packsDir))
                packsDir = Path.Combine(latestGammaDir, "lib", "packs");

            if (Directory.Exists(packsDir))
            {
                _logger.LogInformation("Found packs directory: {Path}", packsDir);

                // Stride packages in vvvv
                var stridePackages = Directory.GetDirectories(packsDir)
                    .Where(d => Path.GetFileName(d).StartsWith("Stride.", StringComparison.OrdinalIgnoreCase) ||
                               Path.GetFileName(d).StartsWith("VL.Stride", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                _logger.LogInformation("Found {Count} Stride/VL.Stride packages in vvvv packs", stridePackages.Count);

                foreach (var package in stridePackages)
                {
                    var packageName = Path.GetFileName(package);
                    var displayPrefix = $"{vvvvVersion}/{packageName}";

                    // First try stride/Assets subfolder (traditional structure)
                    var assetsPath = Path.Combine(package, "stride", "Assets");
                    if (Directory.Exists(assetsPath))
                    {
                        _logger.LogInformation("Found Assets at: {Path}", assetsPath);
                        paths.Add(assetsPath);
                        AddPathDisplayRule(assetsPath, displayPrefix);
                    }
                    else
                    {
                        // Fallback: search directly in package folder (newer structure)
                        // Check if there are .sdsl files anywhere in the package
                        var hasShaders = Directory.EnumerateFiles(package, "*.sdsl", SearchOption.AllDirectories).Any();
                        if (hasShaders)
                        {
                            _logger.LogInformation("Found shaders directly in package: {Path}", package);
                            paths.Add(package);
                            AddPathDisplayRule(package, displayPrefix);
                        }
                    }
                }
            }
            else
            {
                _logger.LogInformation("Packs directory not found");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error discovering vvvv paths");
        }

        return paths;
    }

    #endregion
}

/// <summary>
/// Represents path replacement rules for display paths.
/// </summary>
public class PathDisplayRule
{
    public string FullPathPrefix { get; }
    public string DisplayPrefix { get; }

    public PathDisplayRule(string fullPathPrefix, string displayPrefix)
    {
        FullPathPrefix = fullPathPrefix;
        DisplayPrefix = displayPrefix;
    }
}

/// <summary>
/// Source/scope of a shader - used for filtering suggestions.
/// </summary>
public enum ShaderSource
{
    /// <summary>Stride NuGet packages (core engine shaders)</summary>
    Stride,
    /// <summary>vvvv gamma installation (VL.Stride shaders)</summary>
    Vvvv,
    /// <summary>Current workspace/project (user's local shaders)</summary>
    Workspace
}

public class ShaderInfo
{
    public string Name { get; }
    public string FilePath { get; }
    public string DisplayPath { get; }
    public ShaderSource Source { get; }
    public ParsedShader? Parsed { get; set; }

    public ShaderInfo(string name, string filePath, string displayPath, ShaderSource source = ShaderSource.Stride)
    {
        Name = name;
        FilePath = filePath;
        DisplayPath = displayPath;
        Source = source;
    }
}
