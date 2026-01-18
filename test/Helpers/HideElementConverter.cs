using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace test.Helpers;

public partial class HideElementConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value == null)
        {
            return Visibility.Collapsed;
        }

        // If a parameter is provided, collapse when `value` matches the parameter.
        // This keeps the original semantics (Visible when non-null) when parameter is not used.
        if (parameter != null)
        {
            var paramText = parameter.ToString();
            if (!string.IsNullOrWhiteSpace(paramText))
            {
                // Handle enum values (e.g. DownloadStatus.Pending) and raw strings.
                if (value.GetType().IsEnum)
                {
                    if (string.Equals(value.ToString(), paramText, StringComparison.OrdinalIgnoreCase))
                    {
                        return Visibility.Collapsed;
                    }
                }
                else if (string.Equals(value.ToString(), paramText, StringComparison.OrdinalIgnoreCase))
                {
                    return Visibility.Collapsed;
                }
            }
        }

        return Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}
