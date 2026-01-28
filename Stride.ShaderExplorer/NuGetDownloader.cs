using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace StrideShaderExplorer
{
    /// <summary>
    /// Downloads NuGet packages using available tools (nuget.exe or dotnet CLI).
    /// </summary>
    public class NuGetDownloader
    {
        private static readonly string[] StridePackages = { "Stride.Rendering", "Stride.Graphics" };

        public string NuGetCacheDir { get; }

        public NuGetDownloader()
        {
            NuGetCacheDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".nuget", "packages");
        }

        /// <summary>
        /// Checks if the required Stride packages are installed.
        /// </summary>
        public bool ArePackagesInstalled()
        {
            return StridePackages.All(IsPackageInstalled);
        }

        /// <summary>
        /// Gets list of missing packages.
        /// </summary>
        public string[] GetMissingPackages()
        {
            return StridePackages.Where(p => !IsPackageInstalled(p)).ToArray();
        }

        /// <summary>
        /// Downloads missing Stride packages using available tools.
        /// Returns result indicating success/failure.
        /// </summary>
        public DownloadResult DownloadMissingPackages()
        {
            Directory.CreateDirectory(NuGetCacheDir);

            var missing = GetMissingPackages();
            if (missing.Length == 0)
                return DownloadResult.PackagesAlreadyInstalled();

            var nugetExe = FindNugetExe();
            if (nugetExe != null)
                return DownloadWithNugetExe(nugetExe, missing);

            return DownloadWithDotnetCli(missing);
        }

        private bool IsPackageInstalled(string packageName)
        {
            // Standard nuget cache: stride.rendering/version/
            var lowerDir = Path.Combine(NuGetCacheDir, packageName.ToLowerInvariant());
            if (Directory.Exists(lowerDir) && Directory.GetDirectories(lowerDir).Any())
                return true;

            // ExcludeVersion structure: Stride.Rendering/
            var exactDir = Path.Combine(NuGetCacheDir, packageName);
            return Directory.Exists(exactDir);
        }

        private string FindNugetExe()
        {
            // 1. Check PATH
            if (IsToolAvailable("nuget", "help"))
                return "nuget";

            // 2. Check vvvv installation
            return VvvvPathResolver.Instance.FindNugetExe();
        }

        private bool IsToolAvailable(string fileName, string testArgs)
        {
            try
            {
                var process = Process.Start(new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = testArgs,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                });
                process?.WaitForExit(5000);
                return process?.ExitCode == 0;
            }
            catch { return false; }
        }

        private DownloadResult DownloadWithNugetExe(string nugetExe, string[] packages)
        {
            var errors = new List<string>();

            foreach (var package in packages)
            {
                var (exitCode, _, error) = RunProcess(nugetExe,
                    $"install {package} -OutputDirectory \"{NuGetCacheDir}\" -ExcludeVersion -DependencyVersion ignore");

                if (exitCode != 0)
                    errors.Add($"{package}: {error}");
            }

            return new DownloadResult(errors.Count == 0, errors, packages.Length);
        }

        private DownloadResult DownloadWithDotnetCli(string[] packages)
        {
            if (!IsToolAvailable("dotnet", "--version"))
                return DownloadResult.NoToolsAvailable();

            var tempDir = Path.Combine(Path.GetTempPath(),
                "StrideShaderExplorer_" + Guid.NewGuid().ToString("N")[..8]);

            try
            {
                Directory.CreateDirectory(tempDir);

                var packageRefs = string.Join("\n    ",
                    packages.Select(p => $"<PackageReference Include=\"{p}\" Version=\"4.*\" />"));

                File.WriteAllText(Path.Combine(tempDir, "temp.csproj"),
$@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    {packageRefs}
  </ItemGroup>
</Project>");

                var (exitCode, _, error) = RunProcess("dotnet", "restore", tempDir);

                return exitCode == 0
                    ? new DownloadResult(true, new List<string>(), packages.Length)
                    : new DownloadResult(false, new List<string> { error }, packages.Length);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        private (int exitCode, string output, string error) RunProcess(
            string fileName, string arguments, string workingDir = null)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            if (workingDir != null)
                startInfo.WorkingDirectory = workingDir;

            using var process = Process.Start(startInfo);
            if (process == null)
                return (-1, "", "Failed to start process");

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            return (process.ExitCode, output, error);
        }
    }

    public class DownloadResult
    {
        public bool Success { get; }
        public bool AlreadyInstalled { get; }
        public bool NoToolsFound { get; }
        public List<string> Errors { get; }
        public int TotalPackages { get; }
        public bool PartialSuccess => Success && Errors.Count > 0 && Errors.Count < TotalPackages;

        public DownloadResult(bool success, List<string> errors, int totalPackages)
        {
            Success = success;
            Errors = errors;
            TotalPackages = totalPackages;
        }

        private DownloadResult(bool alreadyInstalled, bool noTools)
        {
            AlreadyInstalled = alreadyInstalled;
            NoToolsFound = noTools;
            Success = alreadyInstalled;
            Errors = new List<string>();
        }

        public static DownloadResult PackagesAlreadyInstalled() => new(true, false);
        public static DownloadResult NoToolsAvailable() => new(false, true);
    }
}
