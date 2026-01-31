using Microsoft.Extensions.Logging;

namespace StrideShaderLanguageServer.Services;

/// <summary>
/// Manages the shader workspace - discovers, indexes, and provides access to all shaders.
/// </summary>
public class ShaderWorkspace
{
    private readonly ILogger<ShaderWorkspace> _logger;
    private readonly ShaderParser _parser;
    private readonly HashSet<string> _workspaceFolders = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Path, ShaderSource Source)> _shaderSearchPaths = new();
    private readonly Dictionary<string, ShaderInfo> _shadersByName = new();
    private readonly Dictionary<string, ShaderInfo> _shadersByPath = new();
    private readonly Dictionary<string, List<string>> _duplicateShaders = new(); // name -> list of all paths
    private readonly Dictionary<string, ParsedShader> _lastValidParse = new();
    private readonly List<PathDisplayRule> _pathDisplayRules = new();
    private readonly object _lock = new();

    // Cached shader names list - invalidated when shaders are added
    private IReadOnlyList<string>? _cachedShaderNames;

    // Background parsing cancellation
    private CancellationTokenSource? _backgroundParseCts;

    public event EventHandler? IndexingComplete;

    /// <summary>
    /// Raised when a shader document is updated (for cache invalidation).
    /// </summary>
    public event Action<string>? DocumentUpdated;

    /// <summary>
    /// Raised when a shader file needs diagnostics published (for background parsing).
    /// </summary>
    public event Action<string>? RequestDiagnosticsPublish;

    public ShaderWorkspace(ILogger<ShaderWorkspace> logger, ShaderParser parser)
    {
        _logger = logger;
        _parser = parser;
    }

    public void AddWorkspaceFolder(string path)
    {
        lock (_lock)
        {
            if (_workspaceFolders.Add(path))
            {
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
            // Clear duplicate tracking for fresh indexing
            _duplicateShaders.Clear();

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
                    // Track all paths for this shader name (for duplicate detection)
                    if (!_duplicateShaders.TryGetValue(name, out var pathList))
                    {
                        pathList = new List<string>();
                        _duplicateShaders[name] = pathList;
                    }
                    pathList.Add(file);

                    // Check for duplicate names and warn
                    if (_shadersByName.TryGetValue(name, out var existing))
                    {
                        // Workspace shaders should take priority over Stride/vvvv shaders
                        if (source == ShaderSource.Workspace && existing.Source != ShaderSource.Workspace)
                        {
                            _logger.LogInformation("Workspace shader '{Name}' overrides {Source} shader at {Path}",
                                name, existing.Source, existing.FilePath);
                        }
                        else if (source == ShaderSource.Workspace && existing.Source == ShaderSource.Workspace)
                        {
                            // Two workspace shaders with same name - warn!
                            _logger.LogWarning("DUPLICATE: Workspace shader '{Name}' exists in multiple locations:\n  - {ExistingPath}\n  - {NewPath}\n  The second one will be used.",
                                name, existing.FilePath, file);
                        }
                        else
                        {
                            _logger.LogDebug("Shader '{Name}' from {NewSource} overrides {OldSource} at {Path}",
                                name, source, existing.Source, existing.FilePath);
                        }
                    }

                    _shadersByName[name] = info;
                    _shadersByPath[file] = info;
                    _cachedShaderNames = null; // Invalidate cache
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error indexing shader file {Path}", file);
            }
        }

        _logger.LogInformation("Indexed {Count} shaders", _shadersByName.Count);

        IndexingComplete?.Invoke(this, EventArgs.Empty);

        // Start background parsing of workspace shaders
        StartBackgroundParsing();
    }

    /// <summary>
    /// Parse workspace shaders in the background so they show diagnostics without being opened.
    /// </summary>
    private void StartBackgroundParsing()
    {
        // Cancel any existing background parse
        _backgroundParseCts?.Cancel();
        _backgroundParseCts = new CancellationTokenSource();
        var ct = _backgroundParseCts.Token;

        // Fire-and-forget background task (discard to suppress warning)
        _ = Task.Run(() =>
        {
            try
            {
                // Get workspace shaders to parse
                List<ShaderInfo> workspaceShaders;
                lock (_lock)
                {
                    workspaceShaders = _shadersByName.Values
                        .Where(s => s.Source == ShaderSource.Workspace)
                        .ToList();
                }

                _logger.LogInformation("Background parsing {Count} workspace shaders", workspaceShaders.Count);

                foreach (var shader in workspaceShaders)
                {
                    if (ct.IsCancellationRequested)
                        break;

                    try
                    {
                        // Request diagnostics for this file (which triggers parsing)
                        RequestDiagnosticsPublish?.Invoke(shader.FilePath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Background parse failed for {Shader}", shader.Name);
                    }
                }

                if (!ct.IsCancellationRequested)
                {
                    _logger.LogInformation("Background parsing complete");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Background parsing error");
            }
        }, ct);
    }

    /// <summary>
    /// Stop background parsing (call on shutdown).
    /// </summary>
    public void StopBackgroundParsing()
    {
        _backgroundParseCts?.Cancel();
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
            // Return cached list if available, otherwise rebuild
            return _cachedShaderNames ??= _shadersByName.Keys.ToList();
        }
    }

    /// <summary>
    /// Check if a shader name has duplicates (multiple files with same name).
    /// </summary>
    public bool HasDuplicates(string shaderName)
    {
        lock (_lock)
        {
            return _duplicateShaders.TryGetValue(shaderName, out var paths) && paths.Count > 1;
        }
    }

    /// <summary>
    /// Get all file paths for a shader name (for showing duplicate locations).
    /// </summary>
    public IReadOnlyList<string> GetAllPathsForShader(string shaderName)
    {
        lock (_lock)
        {
            if (_duplicateShaders.TryGetValue(shaderName, out var paths))
                return paths.ToList();
            return new List<string>();
        }
    }

    /// <summary>
    /// Get the shader that is "closest" to a given file path.
    /// Uses longest common path prefix to determine closeness.
    /// </summary>
    public ShaderInfo? GetClosestShaderByName(string name, string? contextFilePath)
    {
        lock (_lock)
        {
            // If no duplicates or no context, return the default
            if (contextFilePath == null || !_duplicateShaders.TryGetValue(name, out var allPaths) || allPaths.Count <= 1)
            {
                return _shadersByName.TryGetValue(name, out var info) ? info : null;
            }

            // Find the shader with the longest common path prefix
            var contextDir = Path.GetDirectoryName(contextFilePath) ?? "";
            ShaderInfo? closest = null;
            int longestCommonLength = -1;

            foreach (var path in allPaths)
            {
                var shaderDir = Path.GetDirectoryName(path) ?? "";
                var commonLength = GetCommonPathPrefixLength(contextDir, shaderDir);

                if (commonLength > longestCommonLength)
                {
                    longestCommonLength = commonLength;
                    if (_shadersByPath.TryGetValue(path, out var shaderInfo))
                    {
                        closest = shaderInfo;
                    }
                }
            }

            // Fall back to default if no match found
            return closest ?? (_shadersByName.TryGetValue(name, out var defaultInfo) ? defaultInfo : null);
        }
    }

    /// <summary>
    /// Get a parsed shader by name, preferring the one closest to the context file path.
    /// Use this when resolving base shader references to pick the right one among duplicates.
    /// </summary>
    public ParsedShader? GetParsedShaderClosest(string name, string? contextFilePath)
    {
        var info = GetClosestShaderByName(name, contextFilePath);
        if (info == null) return null;

        // Lazy parse - only cache successful full parses, retry partial results
        if (info.Parsed == null || info.Parsed.IsPartial)
        {
            try
            {
                var sourceCode = File.ReadAllText(info.FilePath);
                var newParsed = _parser.TryParse(info.Name, sourceCode);

                // Only update if we got a better result (full parse or first parse)
                if (newParsed != null && (info.Parsed == null || !newParsed.IsPartial))
                {
                    info.Parsed = newParsed;

                    // Cache successful full parse
                    if (!newParsed.IsPartial)
                    {
                        lock (_lock)
                        {
                            _lastValidParse[info.Name] = newParsed;
                        }
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
    /// Calculate the length of the common path prefix between two paths.
    /// </summary>
    private static int GetCommonPathPrefixLength(string path1, string path2)
    {
        // Normalize paths for comparison
        var p1 = path1.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
        var p2 = path2.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();

        // Split into segments
        var segments1 = p1.Split('\\');
        var segments2 = p2.Split('\\');

        int commonSegments = 0;
        int minLength = Math.Min(segments1.Length, segments2.Length);

        for (int i = 0; i < minLength; i++)
        {
            if (segments1[i] == segments2[i])
                commonSegments++;
            else
                break;
        }

        return commonSegments;
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

        // Lazy parse - only cache successful full parses, retry partial results
        if (info.Parsed == null || info.Parsed.IsPartial)
        {
            try
            {
                var sourceCode = File.ReadAllText(info.FilePath);
                var newParsed = _parser.TryParse(info.Name, sourceCode);

                // Only update if we got a better result (full parse or first parse)
                if (newParsed != null && (info.Parsed == null || !newParsed.IsPartial))
                {
                    info.Parsed = newParsed;

                    // Cache successful full parse
                    if (!newParsed.IsPartial)
                    {
                        lock (_lock)
                        {
                            _lastValidParse[info.Name] = newParsed;
                        }
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
                _cachedShaderNames = null; // Invalidate cache for new shader
            }

            // Re-parse with new content
            _parser.InvalidateCache(name);
            var result = _parser.TryParseWithDiagnostics(name, content);

            // Update shader info
            info.Parsed = result.Shader;

            // Notify listeners that document was updated (for cache invalidation)
            DocumentUpdated?.Invoke(name);

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
            // Get all vvvv_gamma_* directories and parse their versions
            var gammaInstalls = Directory.GetDirectories(vvvvBaseDir)
                .Where(d => Path.GetFileName(d).StartsWith("vvvv_gamma_", StringComparison.OrdinalIgnoreCase))
                .Select(d => new { Path = d, Version = ParseVvvvVersion(Path.GetFileName(d)) })
                .Where(v => v.Version != null)  // Filter out unparseable/special versions
                .ToList();

            if (gammaInstalls.Count == 0)
            {
                _logger.LogInformation("No valid vvvv_gamma_* installation found");
                return paths;
            }

            // Pick the highest version (preview is typically newer than stable of same major.minor)
            var latestInstall = gammaInstalls
                .OrderByDescending(v => v.Version!.Major)
                .ThenByDescending(v => v.Version!.Minor)
                .ThenByDescending(v => v.Version!.PreviewNumber ?? -1)  // Preview > Stable for same version
                .First();

            var latestGammaDir = latestInstall.Path;
            var vvvvVersion = latestInstall.Version!.ToDisplayString();  // e.g., "vvvv@7.0" or "vvvv@7.1-144"
            _logger.LogInformation("Using vvvv installation: {Path} (version: {Version})", latestGammaDir, vvvvVersion);

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

    /// <summary>
    /// Parse a vvvv gamma directory name into version components.
    /// Filters out special versions like "-hdr" and handles stable vs preview.
    /// </summary>
    /// <param name="dirName">Directory name like "vvvv_gamma_7.0" or "vvvv_gamma_7.1-0144-g5b48859314-win-x64"</param>
    /// <returns>Parsed version info, or null if this is a special/invalid version to skip</returns>
    private static VvvvVersion? ParseVvvvVersion(string dirName)
    {
        // Skip special versions (e.g., vvvv_gamma_7.1-hdr-0002-...)
        var specialVersions = new[] { "-hdr", "-beta", "-alpha", "-rc", "-test", "-dev" };
        if (specialVersions.Any(s => dirName.Contains(s, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        // Expected format: vvvv_gamma_MAJOR.MINOR[-PREVIEW-HASH-PLATFORM]
        // Examples:
        //   vvvv_gamma_7.0
        //   vvvv_gamma_6.8
        //   vvvv_gamma_7.1-0144-g5b48859314-win-x64
        var prefix = "vvvv_gamma_";
        if (!dirName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var versionPart = dirName.Substring(prefix.Length);

        // Split by dash to separate version from preview/hash/platform
        var parts = versionPart.Split('-');
        var mainVersion = parts[0];  // "7.0" or "7.1"

        var versionParts = mainVersion.Split('.');
        if (versionParts.Length < 2)
            return null;

        if (!int.TryParse(versionParts[0], out var major))
            return null;
        if (!int.TryParse(versionParts[1], out var minor))
            return null;

        // Check if it's a preview version (has additional parts after version)
        int? previewNumber = null;
        if (parts.Length > 1)
        {
            // Second part should be the preview number (e.g., "0144")
            if (int.TryParse(parts[1], out var preview))
            {
                previewNumber = preview;
            }
        }

        return new VvvvVersion(major, minor, previewNumber);
    }

    #endregion
}

/// <summary>
/// Parsed vvvv gamma version information.
/// </summary>
public class VvvvVersion
{
    public int Major { get; }
    public int Minor { get; }
    public int? PreviewNumber { get; }
    public bool IsPreview => PreviewNumber.HasValue;

    public VvvvVersion(int major, int minor, int? previewNumber = null)
    {
        Major = major;
        Minor = minor;
        PreviewNumber = previewNumber;
    }

    /// <summary>
    /// Returns a display string like "vvvv@7.0" or "vvvv@7.1-144"
    /// </summary>
    public string ToDisplayString()
    {
        if (IsPreview)
            return $"vvvv@{Major}.{Minor}-{PreviewNumber}";
        return $"vvvv@{Major}.{Minor}";
    }
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

    /// <summary>
    /// True if this shader is from the user's workspace (editable), false if from Stride/vvvv (read-only).
    /// </summary>
    public bool IsWorkspaceShader => Source == ShaderSource.Workspace;

    public ShaderInfo(string name, string filePath, string displayPath, ShaderSource source = ShaderSource.Stride)
    {
        Name = name;
        FilePath = filePath;
        DisplayPath = displayPath;
        Source = source;
    }
}
