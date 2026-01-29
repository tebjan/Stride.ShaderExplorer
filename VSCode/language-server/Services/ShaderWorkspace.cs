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
    private readonly List<string> _shaderSearchPaths = new();
    private readonly Dictionary<string, ShaderInfo> _shadersByName = new();
    private readonly Dictionary<string, ShaderInfo> _shadersByPath = new();
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

        // 1. NuGet packages cache
        var nugetPath = GetNuGetPackagesPath();
        _logger.LogInformation("NuGet packages path: {Path}", nugetPath ?? "NOT FOUND");
        if (nugetPath != null && Directory.Exists(nugetPath))
        {
            var stridePaths = DiscoverStrideNuGetPackages(nugetPath);
            _logger.LogInformation("Found {Count} Stride NuGet package paths", stridePaths.Count);
            foreach (var path in stridePaths)
            {
                AddShaderSearchPath(path);
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
            AddShaderSearchPath(path);
        }

        // 3. Workspace folders
        _logger.LogInformation("Adding {Count} workspace folders", _workspaceFolders.Count);
        foreach (var folder in _workspaceFolders)
        {
            AddShaderSearchPath(folder);
        }

        _logger.LogInformation("Discovered {Count} total shader search paths", _shaderSearchPaths.Count);
    }

    private void AddShaderSearchPath(string path)
    {
        lock (_lock)
        {
            if (!_shaderSearchPaths.Contains(path))
            {
                _shaderSearchPaths.Add(path);
                _logger.LogInformation("Added shader search path: {Path}", path);
            }
        }
    }

    public void IndexAllShaders()
    {
        _logger.LogInformation("Indexing all shaders...");

        var shaderFiles = new List<string>();

        lock (_lock)
        {
            foreach (var searchPath in _shaderSearchPaths)
            {
                try
                {
                    if (Directory.Exists(searchPath))
                    {
                        var files = Directory.EnumerateFiles(searchPath, "*.sdsl", SearchOption.AllDirectories);
                        shaderFiles.AddRange(files);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error searching for shaders in {Path}", searchPath);
                }
            }
        }

        _logger.LogInformation("Found {Count} shader files", shaderFiles.Count);

        foreach (var file in shaderFiles)
        {
            try
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var info = new ShaderInfo(name, file);

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

        // Log some sample shader names for debugging
        var sampleShaders = _shadersByName.Keys.Take(10).ToList();
        _logger.LogInformation("Sample indexed shaders: {Shaders}", string.Join(", ", sampleShaders));

        // Check specifically for common base shaders
        var commonBases = new[] { "ComputeShaderBase", "ShaderBase", "Texturing", "ColorTarget" };
        foreach (var baseName in commonBases)
        {
            var found = _shadersByName.ContainsKey(baseName);
            _logger.LogInformation("Base shader '{BaseName}': {Status}", baseName, found ? "FOUND" : "NOT FOUND");
        }

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
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error reading shader file {Path}", info.FilePath);
            }
        }

        return info.Parsed;
    }

    public void UpdateDocument(string path, string content)
    {
        var name = Path.GetFileNameWithoutExtension(path);

        lock (_lock)
        {
            if (!_shadersByPath.TryGetValue(path, out var info))
            {
                info = new ShaderInfo(name, path);
                _shadersByName[name] = info;
                _shadersByPath[path] = info;
            }

            // Re-parse with new content
            _parser.InvalidateCache(name);
            info.Parsed = _parser.TryParse(name, content);
        }
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
                // Get the latest version
                var versionDir = Directory.GetDirectories(package)
                    .OrderByDescending(d => d)
                    .FirstOrDefault();

                if (versionDir != null)
                {
                    // Stride shaders are in stride\Assets\ with various subdirectories
                    // (ComputeEffect, Core, Materials, Lights, Shaders, etc.)
                    // So we add the Assets folder itself and search recursively
                    var assetsPath = Path.Combine(versionDir, "stride", "Assets");
                    if (Directory.Exists(assetsPath))
                    {
                        _logger.LogInformation("Found Assets at: {Path}", assetsPath);
                        paths.Add(assetsPath);
                    }

                    // Also check contentFiles locations
                    assetsPath = Path.Combine(versionDir, "contentFiles", "any", "net8.0", "stride", "Assets");
                    if (Directory.Exists(assetsPath))
                    {
                        _logger.LogInformation("Found Assets at: {Path}", assetsPath);
                        paths.Add(assetsPath);
                    }

                    assetsPath = Path.Combine(versionDir, "contentFiles", "any", "net6.0", "stride", "Assets");
                    if (Directory.Exists(assetsPath))
                    {
                        _logger.LogInformation("Found Assets at: {Path}", assetsPath);
                        paths.Add(assetsPath);
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
                    // Search the entire Assets directory (shaders are in various subdirectories)
                    var assetsPath = Path.Combine(package, "stride", "Assets");
                    if (Directory.Exists(assetsPath))
                    {
                        _logger.LogInformation("Found Assets at: {Path}", assetsPath);
                        paths.Add(assetsPath);
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

public class ShaderInfo
{
    public string Name { get; }
    public string FilePath { get; }
    public ParsedShader? Parsed { get; set; }

    public ShaderInfo(string name, string filePath)
    {
        Name = name;
        FilePath = filePath;
    }
}
