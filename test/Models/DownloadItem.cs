using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using StoreListings.Library;

namespace test.Models;

public enum DownloadStatus
{
    Pending,
    Downloading,
    Installing,
    Completed,
    Failed,
    Cancelled,
}

public partial class DownloadItem : INotifyPropertyChanged
{
    private string _productId = string.Empty;
    public string ProductId
    {
        get => _productId;
        set
        {
            if (_productId != value)
            {
                _productId = value;
                OnPropertyChanged();
            }
        }
    }

    private string _title = string.Empty;
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

    private string? _logoUrl;
    public string? LogoUrl
    {
        get => _logoUrl;
        set
        {
            if (_logoUrl != value)
            {
                _logoUrl = value;
                OnPropertyChanged();
            }
        }
    }

    private string _publisherName = string.Empty;
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

    private DownloadStatus _status = DownloadStatus.Pending;
    public DownloadStatus Status
    {
        get => _status;
        set
        {
            if (_status != value)
            {
                _status = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    private double _progress;
    public double Progress
    {
        get => _progress;
        set
        {
            if (Math.Abs(_progress - value) > double.Epsilon)
            {
                _progress = value;
                OnPropertyChanged();
                // Also notify StatusText since it depends on Progress when downloading/installing
                if (_status is DownloadStatus.Downloading or DownloadStatus.Installing)
                {
                    OnPropertyChanged(nameof(StatusText));
                }
            }
        }
    }

    private DateTime _startedAt = DateTime.Now;
    public DateTime StartedAt
    {
        get => _startedAt;
        set
        {
            if (_startedAt != value)
            {
                _startedAt = value;
                OnPropertyChanged();
            }
        }
    }

    private DateTime? _completedAt;
    public DateTime? CompletedAt
    {
        get => _completedAt;
        set
        {
            if (_completedAt != value)
            {
                _completedAt = value;
                OnPropertyChanged();
            }
        }
    }

    private List<string> _downloadedFilePaths = [];

    /// <summary>
    /// List of file paths that were downloaded for this item
    /// </summary>
    public List<string> DownloadedFilePaths
    {
        get => _downloadedFilePaths;
        set
        {
            if (_downloadedFilePaths != value)
            {
                _downloadedFilePaths = value;
                OnPropertyChanged();
            }
        }
    }

    // For storing the original product info to navigate back
    [JsonIgnore]
    public StoreEdgeFDProduct? ProductInfo { get; set; }

    private string? _statusTextOverride;

    [JsonIgnore]
    public string? StatusTextOverride
    {
        get => _statusTextOverride;
        set
        {
            if (_statusTextOverride != value)
            {
                _statusTextOverride = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    [JsonIgnore]
    public string StatusText =>
        Status switch
        {
            DownloadStatus.Pending => !string.IsNullOrWhiteSpace(StatusTextOverride)
                ? StatusTextOverride
                : "Pending",
            DownloadStatus.Downloading => !string.IsNullOrWhiteSpace(StatusTextOverride)
                ? StatusTextOverride
                : "Downloading",
            DownloadStatus.Installing => !string.IsNullOrWhiteSpace(StatusTextOverride)
                ? StatusTextOverride
                : "Installing",
            DownloadStatus.Completed => "Completed",
            DownloadStatus.Failed => "Failed",
            DownloadStatus.Cancelled => "Cancelled",
            _ => "Unknown",
        };

    private long? _receivedBytes;
    [JsonIgnore]
    public long? ReceivedBytes
    {
        get => _receivedBytes;
        set
        {
            if (_receivedBytes != value)
            {
                _receivedBytes = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ProgressDetailsText));
            }
        }
    }

    private long? _totalBytes;
    [JsonIgnore]
    public long? TotalBytes
    {
        get => _totalBytes;
        set
        {
            if (_totalBytes != value)
            {
                _totalBytes = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ProgressDetailsText));
            }
        }
    }

    [JsonIgnore]
    public string ProgressDetailsText
    {
        get
        {
            if (TotalBytes is null or <= 0 || ReceivedBytes is null or < 0)
                return string.Empty;

            return $" • {FormatBytes((long)ReceivedBytes)} / {FormatBytes((long)TotalBytes)}";
        }
    }

    private static string FormatBytes(long bytes)
    {
        const double KB = 1024.0;
        const double MB = KB * 1024.0;
        const double GB = MB * 1024.0;

        if (bytes >= GB)
            return $"{bytes / GB:0.#} GB";
        if (bytes >= MB)
            return $"{bytes / MB:0.#} MB";
        if (bytes >= KB)
            return $"{bytes / KB:0.#} KB";
        return $"{bytes} B";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string propertyName = "") =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
