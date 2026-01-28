using System;
using System.Globalization;
using System.Windows.Data;

namespace StrideShaderExplorer
{
    public class SearchModeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is SearchMode mode)
            {
                return mode switch
                {
                    SearchMode.FilesAndMembers => "Files & Members",
                    SearchMode.FilenameOnly => "Filename Only",
                    SearchMode.MembersOnly => "Members Only",
                    _ => value.ToString()
                };
            }
            return value?.ToString() ?? string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
