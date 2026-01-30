using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;

namespace StrideShaderExplorer
{
    public enum StrideSourceDirMode
    {
        Official,
        Dev
    }

    public enum SearchMode
    {
        FilesAndMembers,
        FilenameOnly,
        MembersOnly
    }

    public class MainViewModel : ObservableRecipient
    {
        private const string StrideEnvironmentVariable = "StrideDir";
        private const string NugetEnvironmentVariable = "NUGET_PACKAGES";

        private readonly NuGetDownloader _nugetDownloader = new();
        private readonly ShaderRepository _shaderRepository = new();
        private readonly ShaderTreeBuilder _shaderTreeBuilder;

        private string _filterText;
        private SearchMode _searchMode = SearchMode.FilesAndMembers;
        private ShaderViewModel _selectedShader;
        private IReadOnlyList<string> _paths;

        public List<string> AdditionalPaths
        {
            get;
            set;
        }

        public IReadOnlyDictionary<string, ShaderViewModel> ShaderMap => _shaderRepository.Shaders;

        public bool FindMember(string name, ShaderViewModel shader, out MemberList mems, out List<ShaderViewModel> scopedShaders)
        {
            return _shaderRepository.FindMember(name, shader, out mems, out scopedShaders);
        }

        /// <summary>
        /// The list of roots of the tree view. This includes all the shaders
        /// that do not inherit from any other shaders.
        /// </summary>
        public List<ShaderViewModel> RootShaders { get; set; }

        /// <summary>
        /// The list of all shaders.
        /// </summary>
        public IEnumerable<ShaderViewModel> AllShaders => ShadersInPostOrder();

        public string FilterText
        {
            get { return _filterText; }
            set
            {
                if (SetProperty(ref _filterText, value))
                    UpdateFiltering();
            }
        }

        public SearchMode SearchMode
        {
            get { return _searchMode; }
            set
            {
                if (SetProperty(ref _searchMode, value))
                    UpdateFiltering();
            }
        }

        public IEnumerable<SearchMode> SearchModeOptions => Enum.GetValues<SearchMode>();

        public bool DirectParentsOnly
        {
            get { return _shaderTreeBuilder.DirectParentsOnly; }
            set
            {
                if (_shaderTreeBuilder.DirectParentsOnly != value)
                {
                    _shaderTreeBuilder.DirectParentsOnly = value;
                    OnPropertyChanged();
                    Refresh();
                }
            }
        }

        public ShaderViewModel SelectedShader
        {
            get { return _selectedShader; }
            set
            {
                if (SetProperty(_selectedShader, value, v => _selectedShader = v))
                {
                }
            }
        }

        private string ResolveNugetPackageDir()
        {
            // check if nuget package dir is set
            var nugetPackageDir = Environment.GetEnvironmentVariable(NugetEnvironmentVariable);
            if (nugetPackageDir != null)
                return nugetPackageDir;

            // try to resolve nuget package dir
            var userDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            nugetPackageDir = Path.Combine(userDir, ".nuget", "packages");
            if (Directory.Exists(nugetPackageDir))
                return nugetPackageDir;

            // try vvvv packs dir
            var vvvvPacksDir = VvvvPathResolver.Instance.GetPacksDir();
            if (vvvvPacksDir != null)
                return vvvvPacksDir;

            // return folder of this program
            return Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
        }

        internal void Refresh()
        {
            try
            {
                List<string> paths = null;
                switch (StrideDirMode)
                {
                    case StrideSourceDirMode.Official:
                        var nugetPackageDir = ResolveNugetPackageDir();
                        var strideDirs = Directory.GetDirectories(nugetPackageDir)
                            .Where(dir => Path.GetFileName(dir).StartsWith("stride", StringComparison.OrdinalIgnoreCase))
                            .Where(dir => Directory.EnumerateFileSystemEntries(dir).Any());

                        paths = new List<string>();
                        foreach (var dir in strideDirs)
                        {
                            var subDirs = Directory.GetDirectories(dir).Where(subdir => !subdir.EndsWith("-dev")).ToList();
                            if (subDirs.Count > 0)
                            {
                                // Standard nuget cache structure: stride.rendering/4.0.0.1234/
                                var latest = subDirs.OrderBy(d => d, StringComparer.OrdinalIgnoreCase).LastOrDefault();
                                if (latest != null) paths.Add(latest);
                            }
                            else
                            {
                                // ExcludeVersion download: Stride.Rendering/ (no version subfolder)
                                paths.Add(dir);
                            }
                        }

                        // If no Stride packages found in nuget, try vvvv's packs folder
                        if (paths.Count == 0)
                        {
                            var vvvvPaths = VvvvPathResolver.Instance.GetStridePackagePaths();
                            if (vvvvPaths.Count > 0)
                                paths = vvvvPaths;
                        }

                        // If still no packages found, offer to download via NuGet
                        if (paths.Count == 0)
                        {
                            var result = MessageBox.Show(
                                "No Stride shader packages found.\n\n" +
                                "Would you like to download them via NuGet?\n" +
                                "(Requires nuget CLI to be installed)",
                                "Stride Packages Missing",
                                MessageBoxButton.YesNo,
                                MessageBoxImage.Question);

                            if (result == MessageBoxResult.Yes)
                            {
                                if (DownloadStridePackages())
                                {
                                    // Re-scan for packages after download
                                    strideDirs = Directory.GetDirectories(nugetPackageDir)
                                        .Where(dir => Path.GetFileName(dir).StartsWith("stride", StringComparison.OrdinalIgnoreCase))
                                        .Where(dir => Directory.EnumerateFileSystemEntries(dir).Any());

                                    foreach (var dir in strideDirs)
                                    {
                                        var subDirs = Directory.GetDirectories(dir).Where(subdir => !subdir.EndsWith("-dev")).ToList();
                                        if (subDirs.Count > 0)
                                        {
                                            var latest = subDirs.OrderBy(d => d, StringComparer.OrdinalIgnoreCase).LastOrDefault();
                                            if (latest != null) paths.Add(latest);
                                        }
                                        else
                                        {
                                            paths.Add(dir);
                                        }
                                    }
                                }
                            }
                        }
                        break;
                    case StrideSourceDirMode.Dev:
                        var strideDir = Environment.GetEnvironmentVariable(StrideEnvironmentVariable);
                        if (strideDir != null)
                        {
                            paths = new List<string> { strideDir };
                        }
                        else
                        {
                            var dialog = new System.Windows.Forms.FolderBrowserDialog();
                            dialog.Description = "\"StrideDir\" environment variable not found. Select source repo main folder manually.";
                            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                            {
                                paths = new List<string> { dialog.SelectedPath };
                            }
                            //basePath = System.IO.Path.Combine(basePath, "sources", "engine", "Stride.Engine", "Rendering");
                        }
                        break;
                    default:
                        break;
                }

                if (paths != null)
                    paths.AddRange(AdditionalPaths);
                else
                    paths = AdditionalPaths;

                Paths = paths;
            }
            catch (Exception e)
            {
                MessageBox.Show(e.ToString(), "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        internal void SaveUserSetting()
        {
            Properties.UserSettings.Default.AdditionalPaths = string.Join(";", AdditionalPaths.Where(p => p != "New path..."));
            Properties.UserSettings.Default.Save();
        }

        /// <summary>
        /// Path to the Stride installation folder.
        /// </summary>
        public IReadOnlyList<string> Paths
        {
            get { return _paths; }
            set
            {
                if (SetProperty(ref _paths, value))
                {
                    try
                    {
                        RootShaders = _shaderTreeBuilder.BuildTree(value);
                        OnPropertyChanged(nameof(RootShaders));
                        OnPropertyChanged(nameof(AllShaders));
                        UpdateFiltering();
                        ExpandAll(false);
                    }
                    catch (Exception e)
                    {
                        MessageBox.Show(e.ToString(), "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
        }

        public StrideSourceDirMode StrideDirMode { get; internal set; }

        public MainViewModel()
        {
            _shaderTreeBuilder = new ShaderTreeBuilder(_shaderRepository);

            AdditionalPaths = Properties.UserSettings.Default.AdditionalPaths.Split(';').ToList();

            // Auto-detect vvvv paths on first run
            if (!Properties.UserSettings.Default.VvvvPathsDetected)
            {
                var vvvvPaths = VvvvPathResolver.Instance.GetVLStrideShaderPaths();
                foreach (var path in vvvvPaths)
                {
                    if (!AdditionalPaths.Contains(path))
                        AdditionalPaths.Add(path);
                }
                Properties.UserSettings.Default.VvvvPathsDetected = true;
                Properties.UserSettings.Default.Save();
            }

            AdditionalPaths.Add("New path...");
            Refresh();
        }

        private bool DownloadStridePackages()
        {
            var missing = _nugetDownloader.GetMissingPackages();
            if (missing.Length == 0)
            {
                MessageBox.Show("All Stride packages are already installed.",
                    "Already Installed", MessageBoxButton.OK, MessageBoxImage.Information);
                return true;
            }

            MessageBox.Show(
                $"Downloading {missing.Length} package(s):\n\n{string.Join("\n", missing)}\n\nClick OK to start.",
                "Downloading", MessageBoxButton.OK, MessageBoxImage.Information);

            var result = _nugetDownloader.DownloadMissingPackages();

            if (result.NoToolsFound)
            {
                MessageBox.Show(
                    "Neither nuget.exe nor dotnet CLI found.\n\nPlease install .NET SDK or nuget.exe.",
                    "No Tools Found", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            if (result.Success)
            {
                MessageBox.Show("Stride packages downloaded successfully!",
                    "Download Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                return true;
            }

            MessageBox.Show($"Download failed:\n{string.Join("\n", result.Errors)}",
                "Download Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        public void ExportShaderHierarchy(string filePath)
        {
            var export = new HierarchyExport
            {
                ExportedAt = DateTime.Now.ToString("o"),
                ShaderCount = _shaderRepository.Count,
                RootShaders = RootShaders.Select(ShaderToExport).ToList()
            };

            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(export, options);
            File.WriteAllText(filePath, json);
        }

        private ShaderExport ShaderToExport(ShaderViewModel shader)
        {
            return new ShaderExport
            {
                Name = shader.Name,
                Path = shader.Path,
                BaseShaders = shader.BaseShaders.Select(b => b.Name).ToList(),
                Variables = shader.ParsedShader?.Variables?
                    .Select(v => new MemberExport { Name = v.Name?.Text, Type = v.Type?.Name?.Text })
                    .ToList() ?? new List<MemberExport>(),
                Methods = shader.ParsedShader?.Methods?
                    .Select(m => new MemberExport { Name = m.Name?.Text, Type = m.ReturnType?.Name?.Text ?? "void" })
                    .ToList() ?? new List<MemberExport>(),
                DerivedShaders = shader.TreeViewChildren.Select(ShaderToExport).ToList()
            };
        }

        private void UpdateFiltering()
        {
            foreach (var shader in AllShaders)
            {
                if (string.IsNullOrEmpty(_filterText))
                {
                    shader.IsVisible = true;
                    // Don't change IsExpanded when filter is empty
                }
                else
                {
                    bool matchesName = shader.Name.Contains(_filterText, StringComparison.OrdinalIgnoreCase);
                    bool matchesMember = false;

                    if (_searchMode != SearchMode.FilenameOnly && shader.ParsedShader != null)
                    {
                        matchesMember = shader.ParsedShader.Variables?.Any(v =>
                            v.Name?.Text?.Contains(_filterText, StringComparison.OrdinalIgnoreCase) == true) == true ||
                            shader.ParsedShader.Methods?.Any(m =>
                                m.Name?.Text?.Contains(_filterText, StringComparison.OrdinalIgnoreCase) == true) == true;
                    }

                    shader.IsVisible = _searchMode switch
                    {
                        SearchMode.FilesAndMembers => matchesName || matchesMember,
                        SearchMode.FilenameOnly => matchesName,
                        SearchMode.MembersOnly => matchesMember,
                        _ => matchesName || matchesMember
                    };

                    // Auto-expand parents of visible items only when filtering
                    shader.IsExpanded = shader.DerivedShaders.Any(o => o.IsVisible);
                }
            }
        }

        public void ExpandAll(bool expand)
        {
            foreach (var shader in AllShaders)
                shader.IsExpanded = expand;
        }

        private IEnumerable<ShaderViewModel> ShadersInPostOrder()
        {
            foreach (var rootShader in RootShaders)
                foreach (var shader in ShadersInPostOrder(rootShader))
                    yield return shader;
        }

        private static IEnumerable<ShaderViewModel> ShadersInPostOrder(ShaderViewModel shader)
        {
            foreach (var child in shader.DerivedShaders)
                foreach (var s in ShadersInPostOrder(child))
                    yield return s;
            yield return shader;
        }

    }

    public class ShaderExport
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public List<string> BaseShaders { get; set; }
        public List<MemberExport> Variables { get; set; }
        public List<MemberExport> Methods { get; set; }
        public List<ShaderExport> DerivedShaders { get; set; }
    }

    public class MemberExport
    {
        public string Name { get; set; }
        public string Type { get; set; }
    }

    public class HierarchyExport
    {
        public string ExportedAt { get; set; }
        public int ShaderCount { get; set; }
        public List<ShaderExport> RootShaders { get; set; }
    }
}
