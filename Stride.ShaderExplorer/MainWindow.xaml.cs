using AurelienRibon.Ui.SyntaxHighlightBox;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace StrideShaderExplorer
{
    public partial class MainWindow : Window
    {
        public MainViewModel ViewModel { get; } = new MainViewModel();

        public MainWindow()
        {
            InitializeComponent();
            DataContext = ViewModel;
            Title = "Shader Explorer for Stride " + Assembly.GetEntryAssembly().GetName().Version;
            codeView.CurrentHighlighter = HighlighterManager.Instance.Highlighters["SDSL"];
            memberView.CurrentHighlighter = HighlighterManager.Instance.Highlighters["SDSL"];
            StrideDirMode.ItemsSource = Enum.GetValues(typeof(StrideSourceDirMode)).Cast<StrideSourceDirMode>();
            StrideDirMode.SelectedIndex = 0;
            StrideDirMode.SelectionChanged += StrideDirMode_SelectionChanged;

            codeView.SelectionChanged += CodeView_SelectionChanged;
            BaseShaders.SelectionChanged += Shaders_SelectionChanged;
            DerivedShaders.SelectionChanged += Shaders_SelectionChanged;
            MemberShaders.SelectionChanged += Shaders_SelectionChanged;
            PreviewMouseDown += MainWindow_PreviewMouseDown;
        }

        private void MainWindow_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            switch (e.ChangedButton)
            {
                case MouseButton.XButton1://Back button
                    Back();
                    break;
                case MouseButton.XButton2://forward button
                    Forward();
                    break;
                default:
                    break;
            }
        }

        private void Shaders_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            NavigateToShader(e.AddedItems.OfType<ShaderViewModel>().FirstOrDefault());
        }
       
        private void NavigateToShader(ShaderViewModel s)
        {
            if (s != null)
            {
                SetShaderCode(s);
                forwardStack.Push(s);
            }
        }

        private void SetShaderCode(ShaderViewModel shader, bool isNavigation = false)
        {
            if (shader != null)
            {
                ViewModel.SelectedShader = shader;

                if (!isNavigation)
                {
                    backStack.Push(shader);
                    BackButton.IsEnabled = backStack.Count > 1;
                    forwardStack.Clear();
                    ForwardButton.IsEnabled = false;
                }
            }

        }

        Stack<ShaderViewModel> backStack = new Stack<ShaderViewModel>();
        Stack<ShaderViewModel> forwardStack = new Stack<ShaderViewModel>();

        public void Back()
        {
            if (backStack.Count < 2)
                return;

            var s = backStack.Pop();
            var l = backStack.Peek();
            BackButton.IsEnabled = backStack.Count > 1;

            if (l != null)
            {
                SetShaderCode(l, isNavigation: true);
                forwardStack.Push(s);
                ForwardButton.IsEnabled = true;
            }
        }

        public void Forward()
        {
            if (forwardStack.Count < 1)
                return;

            var s = forwardStack.Pop();
            ForwardButton.IsEnabled = forwardStack.Count > 0;

            if (s != null)
            {
                SetShaderCode(s, isNavigation: true);
                backStack.Push(s);
                BackButton.IsEnabled = true;
            }
        }

        private void CodeView_SelectionChanged(object sender, RoutedEventArgs e)
        {
            var length = codeView.Text.Length;

            if (length < 1)
                return;

            var start = codeView.CaretIndex;
            var end = codeView.CaretIndex;

            while (start >= 0 && start < length)
            {
                if (IsBorderChar(codeView.Text[start]))
                {
                    start++;
                    break;
                }

                start--;
            }

            while (end >= 0 && end < (length - 1))
            {
                if (IsBorderChar(codeView.Text[end]))
                    break;
                end++;
            }

            var word = codeView.Text.Substring(Math.Max(start, 0), Math.Max(end - start, 0));

            if (!string.IsNullOrWhiteSpace(word))
            {
                if (ViewModel.FindMember(word, ViewModel.SelectedShader, out var mems, out var scopedShaders))
                {
                    ViewModel.SelectedShader.SelectedMember = mems;
                    ViewModel.SelectedShader.ScopedShaders = scopedShaders;
                }
            }

            bool IsBorderChar(char c)
                => !(c == '_' || char.IsLetterOrDigit(c));
        }

        private void StrideDirMode_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            ViewModel.StrideDirMode = (StrideSourceDirMode)StrideDirMode.SelectedIndex;
            ViewModel.Refresh();
        }

        private void TreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            SetShaderCode(e.NewValue as ShaderViewModel);
        }

        private void OnExpandAllButtonClick(object sender, RoutedEventArgs e)
        {
            ViewModel.ExpandAll(true);
        }

        private void OnCollapseAllButtonClick(object sender, RoutedEventArgs e)
        {
            ViewModel.ExpandAll(false);
        }

        private void OnInfoButtonClick(object sender, RoutedEventArgs e)
        {
            new InfoWindow().ShowDialog();
        }

        private void OnBackButtonClick(object sender, RoutedEventArgs e)
        {
            Back();
        }

        private void OnForwardButtonClick(object sender, RoutedEventArgs e)
        {
            Forward();
        }

        private void OnExploreButtonClick(object sender, RoutedEventArgs e)
        {
            if (backStack.Count > 0)
            {
                // suppose that we have a test.txt at E:\
                string filePath = backStack.Peek()?.Path;
                if (!File.Exists(filePath))
                {
                    return;
                }

                // combine the arguments together
                // it doesn't matter if there is a space after ','
                var argument = "/select, \"" + filePath + "\"";

                System.Diagnostics.Process.Start("explorer.exe", argument);
            }
        }

        private void OnAddDirsButtonClick(object sender, RoutedEventArgs e)
        {
            var pw = new AdditionalPathsWindow();
            pw.DataContext = ViewModel;
            pw.ShowDialog();
            ViewModel.Refresh();
        }

        private void OnExportButtonClick(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "JSON Files (*.json)|*.json",
                DefaultExt = ".json",
                FileName = "ShaderHierarchy"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    ViewModel.ExportShaderHierarchy(dialog.FileName);
                    MessageBox.Show($"Exported {ViewModel.shaders.Count} shaders to:\n{dialog.FileName}",
                        "Export Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Export failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            ViewModel.SaveUserSetting();
            base.OnClosed(e);
        }
    }
}
