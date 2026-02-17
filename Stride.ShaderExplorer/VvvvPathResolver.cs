using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace StrideShaderExplorer
{
    /// <summary>
    /// Resolves paths within vvvv gamma installations.
    /// </summary>
    public class VvvvPathResolver
    {
        private static readonly Lazy<VvvvPathResolver> _instance = new(() => new VvvvPathResolver());
        public static VvvvPathResolver Instance => _instance.Value;

        public string VvvvBaseDir { get; }
        public string LatestGammaDir { get; }
        public bool IsVvvvInstalled => LatestGammaDir != null;

        private VvvvPathResolver()
        {
            VvvvBaseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "vvvv");

            // Collect candidate directories from filesystem and registry
            var candidateDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Source 1: Default Program Files location
            if (Directory.Exists(VvvvBaseDir))
            {
                foreach (var dir in Directory.GetDirectories(VvvvBaseDir))
                {
                    if (Path.GetFileName(dir).StartsWith("vvvv_gamma_", StringComparison.OrdinalIgnoreCase))
                        candidateDirs.Add(dir);
                }
            }

            // Source 2: Windows registry (Inno Setup uninstall entries)
            try
            {
                const string uninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
                using var key = Registry.LocalMachine.OpenSubKey(uninstallKey);
                if (key != null)
                {
                    foreach (var subKeyName in key.GetSubKeyNames())
                    {
                        if (!subKeyName.StartsWith("vvvv_gamma_", StringComparison.OrdinalIgnoreCase))
                            continue;

                        using var subKey = key.OpenSubKey(subKeyName);
                        var installLocation = subKey?.GetValue("InstallLocation") as string;
                        if (!string.IsNullOrWhiteSpace(installLocation) && Directory.Exists(installLocation))
                            candidateDirs.Add(installLocation.TrimEnd('\\', '/'));
                    }
                }
            }
            catch
            {
                // Ignore registry access errors
            }

            LatestGammaDir = candidateDirs
                .Where(Directory.Exists)
                .OrderByDescending(d => d)
                .FirstOrDefault();
        }

        /// <summary>
        /// Finds nuget.exe in vvvv installation.
        /// </summary>
        public string FindNugetExe()
        {
            if (LatestGammaDir == null)
                return null;

            var searchPaths = new[]
            {
                Path.Combine(LatestGammaDir, "nuget.exe"),
                Path.Combine(LatestGammaDir, "lib", "nuget.exe"),
                Path.Combine(LatestGammaDir, "tools", "nuget.exe")
            };

            return searchPaths.FirstOrDefault(File.Exists);
        }

        /// <summary>
        /// Gets the packs directory for Stride packages in vvvv.
        /// </summary>
        public string GetPacksDir()
        {
            if (LatestGammaDir == null)
                return null;

            // New structure: vvvv_gamma_*/packs/
            var packsDir = Path.Combine(LatestGammaDir, "packs");
            if (Directory.Exists(packsDir))
                return packsDir;

            // Old structure: vvvv_gamma_*/lib/packs/
            var libPacksDir = Path.Combine(LatestGammaDir, "lib", "packs");
            if (Directory.Exists(libPacksDir))
                return libPacksDir;

            return null;
        }

        /// <summary>
        /// Finds Stride package directories in vvvv's packs folder.
        /// </summary>
        public List<string> GetStridePackagePaths()
        {
            var paths = new List<string>();
            var packsDir = GetPacksDir();

            if (packsDir == null)
                return paths;

            try
            {
                paths.AddRange(Directory.GetDirectories(packsDir)
                    .Where(d => Path.GetFileName(d).StartsWith("Stride.", StringComparison.OrdinalIgnoreCase)));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
            {
                // Ignore access errors
            }

            return paths;
        }

        /// <summary>
        /// Finds VL.Stride shader effect paths in vvvv.
        /// </summary>
        public List<string> GetVLStrideShaderPaths()
        {
            var paths = new List<string>();

            if (LatestGammaDir == null)
                return paths;

            try
            {
                // New structure: vvvv_gamma_*/packs/VL.Stride.Runtime/stride/Assets/Effects
                var packsDir = Path.Combine(LatestGammaDir, "packs");
                if (Directory.Exists(packsDir))
                {
                    var runtimeDir = Path.Combine(packsDir, "VL.Stride.Runtime");
                    if (Directory.Exists(runtimeDir))
                    {
                        var effectsPath = Path.Combine(runtimeDir, "stride", "Assets", "Effects");
                        if (Directory.Exists(effectsPath))
                        {
                            paths.Add(effectsPath);
                            return paths;
                        }
                    }
                }

                // Old structure: vvvv_gamma_*/lib/packs/VL.Stride.Runtime.*/stride/Assets/Effects
                var libPacksDir = Path.Combine(LatestGammaDir, "lib", "packs");
                if (Directory.Exists(libPacksDir))
                {
                    var runtimeDir = Directory.GetDirectories(libPacksDir)
                        .Where(d => Path.GetFileName(d).StartsWith("VL.Stride.Runtime", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(d => d)
                        .FirstOrDefault();

                    if (runtimeDir != null)
                    {
                        var effectsPath = Path.Combine(runtimeDir, "stride", "Assets", "Effects");
                        if (Directory.Exists(effectsPath))
                            paths.Add(effectsPath);
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
            {
                // Ignore access errors
            }

            return paths;
        }
    }
}
