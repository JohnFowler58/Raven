using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Raven.Helpers;

public partial class HideElementConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (IsEmptyOrZero(value) || value is null)
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
                    if (
                        string.Equals(
                            value.ToString(),
                            paramText,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    {
                        return Visibility.Collapsed;
                    }
                }
                else if (
                    string.Equals(value.ToString(), paramText, StringComparison.OrdinalIgnoreCase)
                )
                {
                    return Visibility.Collapsed;
                }
            }
        }

        return Visibility.Visible;
    }

    private static bool IsEmptyOrZero(object value)
    {
        if (value is null)
        {
            return true;
        }

        if (value is string s)
        {
            return string.IsNullOrWhiteSpace(s);
        }

        if (value is System.Collections.ICollection collection)
        {
            return collection.Count == 0;
        }

        return value switch
        {
            byte v => v == 0,
            sbyte v => v == 0,
            short v => v == 0,
            ushort v => v == 0,
            int v => v == 0,
            uint v => v == 0,
            long v => v == 0,
            ulong v => v == 0,
            float v => v == 0,
            double v => v == 0,
            decimal v => v == 0,
            _ => false,
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}
