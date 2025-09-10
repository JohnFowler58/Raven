using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using StoreListings.Library;

namespace test.Models;

public partial class AppInfo : INotifyPropertyChanged
{
    public AppInfo()
    {
        _productID = string.Empty;
        _logo = null;
        _screenshots = [];
        _lastUpdated = null;
        _title = string.Empty;
        _publisherName = string.Empty;
        _description = string.Empty;
        _rating = null;
        _ratingCount = null;
        _size = string.Empty;
    }

    public void SetValues(
        string productID,
        Image logo,
        List<Image> screenshots,
        string? lastUpdated,
        string title,
        string publisherName,
        string? description,
        double? rating,
        long? ratingCount,
        long? size
    )
    {
        ProductID = productID;
        Logo = logo;
        Screenshots = screenshots;
        LastUpdated = lastUpdated;
        Title = title;
        PublisherName = publisherName;
        Description = description;
        Rating = rating;
        RatingCount = FormatRatingCount(ratingCount);
        Size = FormatSize(size);
    }

    private string _productID;
    public string ProductID
    {
        get => _productID;
        set
        {
            if (_productID != value)
            {
                _productID = value;
                OnPropertyChanged();
            }
        }
    }

    private Image? _logo;
    public Image? Logo
    {
        get => _logo;
        set
        {
            if (_logo != value)
            {
                _logo = value;
                OnPropertyChanged();
            }
        }
    }

    private List<Image> _screenshots;
    public List<Image> Screenshots
    {
        get => _screenshots;
        set
        {
            if (_screenshots != value)
            {
                _screenshots = value;
                OnPropertyChanged();
            }
        }
    }

    private string? _lastUpdated;
    public string? LastUpdated
    {
        get
        {
            if (!string.IsNullOrEmpty(_lastUpdated))
            {
                try
                {
                    return DateTime.Parse(_lastUpdated).ToString("MMMM dd, yyyy");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex.Message);
                }
            }
            return "N/A";
        }
        set
        {

            if (_lastUpdated != value)
            {
                _lastUpdated = value;
                OnPropertyChanged();
            }
        }
    }

    private string _title;
    public string Title
    {
        get => _title;
        set
        {
            if (_title != value)
            {
                _title = value;
                OnPropertyChanged();
            }
        }
    }

    private string _publisherName;
    public string PublisherName
    {
        get => _publisherName;
        set
        {
            if (_publisherName != value)
            {
                _publisherName = value;
                OnPropertyChanged();
            }
        }
    }

    private string? _description;
    public string? Description
    {
        get => _description ?? "N/A";
        set
        {
            if (_description != value)
            {
                _description = value;
                OnPropertyChanged();
            }
        }
    }

    private double? _rating;
    public double? Rating
    {
        get => _rating;
        set
        {
            if (_rating != value)
            {
                _rating = value;
                OnPropertyChanged();
            }
        }
    }

    private string? _ratingCount;
    public string? RatingCount
    {
        get => _ratingCount;
        set
        {
            if (_ratingCount != value)
            {
                _ratingCount = value;
                OnPropertyChanged();
            }
        }
    }

    private string? _size;
    public string? Size
    {
        get => _size ?? "N/A";
        set
        {
            if (_size != value)
            {
                _size = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string propertyName = "") =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static string? FormatRatingCount(long? val)
    {
        if (val.HasValue)
        {
            var count = val.Value;
            if (count >= 1_000_000_000)
                return (count / 1_000_000_000D).ToString("0.#") + "B";
            if (count >= 1_000_000)
                return (count / 1_000_000D).ToString("0.#") + "M";
            if (count >= 1_000)
                return (count / 1_000D).ToString("0") + "K";
            if (count > 0)
                return count.ToString();
        }
        return null;
    }

    private static string? FormatSize(long? val)
    {
        if (val.HasValue)
        {
            var sizeInBytes = val.Value;
            if (sizeInBytes >= 1_000_000_000_000)
                return (sizeInBytes / 1_000_000_000_000D).ToString("0.#") + " TB";
            if (sizeInBytes >= 1_000_000_000)
                return (sizeInBytes / 1_000_000_000D).ToString("0.#") + " GB";
            if (sizeInBytes >= 1_000_000)
                return (sizeInBytes / 1_000_000D).ToString("0.#") + " MB";
            if (sizeInBytes >= 1_000)
                return (sizeInBytes / 1_000D).ToString("0.#") + " KB";
            return sizeInBytes + " B";
        }
        return null;
    }
}
