using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;

namespace StrideShaderExplorer
{
    public partial class AdditionalPathsWindow : Window
    {
        public AdditionalPathsWindow()
        {
            InitializeComponent();
        }

        private void OnPathBrowse(object sender, RoutedEventArgs e)
        {
            var mvm = (MainViewModel)DataContext;
            var item = (sender as Button)?.Tag as string;
            var index = mvm.AdditionalPaths.IndexOf(item);

            var dialog = new System.Windows.Forms.FolderBrowserDialog();

            if (Directory.Exists(item))
                dialog.SelectedPath = item;

            dialog.Description = "Select shader folder";
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                mvm.AdditionalPaths.Remove(item);
                mvm.AdditionalPaths.Insert(index, dialog.SelectedPath);

                if (item == "New path...")
                    mvm.AdditionalPaths.Add("New path...");
                Paths.Items.Refresh();
            }
        }

        private void OnRemove(object sender, RoutedEventArgs e)
        {
            var mvm = (MainViewModel)DataContext;

            var item = (sender as Button)?.Tag as string;

            if (item == "New path...")
                return;

            mvm.AdditionalPaths.Remove(item);
            Paths.Items.Refresh();
        }

        private void OnCloseButtonClick(object sender, RoutedEventArgs e) => Close();

        private void OnClearAllClick(object sender, RoutedEventArgs e)
        {
            var mvm = (MainViewModel)DataContext;
            mvm.AdditionalPaths.Clear();
            mvm.AdditionalPaths.Add("New path...");
            Paths.Items.Refresh();
        }

        private void OnDetectVvvvPathsClick(object sender, RoutedEventArgs e)
        {
            var mvm = (MainViewModel)DataContext;
            var detectedPaths = mvm.DetectVvvvShaderPaths();

            if (detectedPaths.Count == 0)
            {
                MessageBox.Show("No vvvv shader directories found.\n\nExpected paths:\n" +
                    "New: C:\\Program Files\\vvvv\\vvvv_gamma_X.X\\packs\\VL.Stride.Runtime\\stride\\Assets\\Effects\n" +
                    "Old: C:\\Program Files\\vvvv\\vvvv_gamma_X.X\\lib\\packs\\VL.Stride.Runtime.*\\stride\\Assets\\Effects",
                    "No Paths Found", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            int added = 0;
            foreach (var path in detectedPaths)
            {
                if (!mvm.AdditionalPaths.Contains(path))
                {
                    int idx = mvm.AdditionalPaths.IndexOf("New path...");
                    if (idx >= 0) mvm.AdditionalPaths.Insert(idx, path);
                    else mvm.AdditionalPaths.Add(path);
                    added++;
                }
            }
            Paths.Items.Refresh();

            MessageBox.Show(added > 0 ? $"Added {added} vvvv shader path(s)." : "All detected paths already added.",
                added > 0 ? "Paths Detected" : "No New Paths", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
