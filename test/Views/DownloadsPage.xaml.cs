using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using test.Contracts.Services;
using test.Models;
using test.Services;
using test.ViewModels;
using System.IO;

namespace test.Views;

public sealed partial class DownloadsPage : Page
{
    public DownloadsViewModel ViewModel { get; }
    private readonly INavigationService _navigationService;
    private test.Helpers.DownloadItemStatusAnimator? _animator;

    public DownloadsPage()
    {
        ViewModel = App.GetService<DownloadsViewModel>();
        _navigationService = App.GetService<INavigationService>();
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        DownloadManagerService.Instance.BeginObserving();

        _animator ??= new test.Helpers.DownloadItemStatusAnimator(this.DispatcherQueue);

        // When navigating between pages, the per-download animator instances used during download/install
        // may get GC'ed. Restart animations for any active items so the Downloads list doesn't look stuck.
        foreach (var item in ViewModel.Downloads)
        {
            switch (item.Status)
            {
                case DownloadStatus.Pending:
                    _animator.Start(item, GetAnimatorBaseText(item, "Fetching download URLs"));
                    break;
                case DownloadStatus.Downloading:
                    _animator.Start(item, GetAnimatorBaseText(item, "Downloading"));
                    break;
                case DownloadStatus.Installing:
                    _animator.Start(item, GetAnimatorBaseText(item, "Installing"));
                    break;
                case DownloadStatus.Cancelling:
                    _animator.Start(item, GetAnimatorBaseText(item, "Cancelling"));
                    break;
                default:
                    _animator.Stop(item);
                    break;
            }
        }
    }

    private static string GetAnimatorBaseText(DownloadItem item, string fallback)
    {
        var text = item.StatusTextOverride ?? item.StatusText;
        if (string.IsNullOrWhiteSpace(text))
            return fallback;

        var trimmed = text.TrimEnd('.', ' ');
        return string.IsNullOrWhiteSpace(trimmed) ? fallback : trimmed;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        DownloadManagerService.Instance.EndObserving();

        if (_animator != null)
        {
            foreach (var item in ViewModel.Downloads)
            {
                _animator.Stop(item);
            }
        }
    }

    private void DownloadsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is DownloadItem item)
        {
            // Pass the DownloadItem - AppPage will handle fetching product if needed
            _navigationService.NavigateTo(typeof(AppViewModel).FullName!, item);
        }
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var downloadsPath = Path.Combine(AppContext.BaseDirectory, "downloads");
        Directory.CreateDirectory(downloadsPath);

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = downloadsPath,
            UseShellExecute = true
        };

        System.Diagnostics.Process.Start(psi);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string productId)
        {
            DownloadManagerService.Instance.CancelDownload(productId);
        }
    }

    private void DeleteMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem menuItem && menuItem.Tag is string productId)
        {
            DownloadManagerService.Instance.RemoveDownload(productId);
        }
    }

}
