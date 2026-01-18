using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using StoreListings.Library;
using test.Helpers;
using test.Models;
using test.Services;
using test.ViewModels;

namespace test.Views;

public sealed partial class AppPage : Page
{
    public AppViewModel ViewModel { get; }
    public AppInfo AppData { get; set; } = new();
    public UIUpdateService UpdateService { get; }

    private CancellationTokenSource? _cts;
    private StoreEdgeFDProduct? _currentProductInfo;
    private DownloadItem? _activeDownloadItem;

    private static readonly string[] InstallableExtensions =
    [
        ".msix",
        ".appx",
        ".msixbundle",
        ".appxbundle",
    ];

    public AppPage()
    {
        ViewModel = App.GetService<AppViewModel>();
        InitializeComponent();
        UpdateService = new UIUpdateService(this.DispatcherQueue);
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        var (productInfo, productId) = e.Parameter switch
        {
            StoreEdgeFDProduct p => (p, (string?)null),
            DownloadItem { ProductInfo: not null } d => (d.ProductInfo, (string?)null),
            DownloadItem d => (null, d.ProductId),
            _ => ((StoreEdgeFDProduct?)null, (string?)null),
        };

        if (productInfo != null)
        {
            LoadProduct(productInfo);
        }
        else if (productId != null)
        {
            await FetchAndLoadProductAsync(productId);
        }
    }

    private void LoadProduct(StoreEdgeFDProduct productInfo)
    {
        _currentProductInfo = productInfo;

        AppData.SetValues(
            productInfo.ProductId,
            productInfo.Logo,
            productInfo.Screenshots,
            productInfo.RevisionId,
            productInfo.Title,
            productInfo.PublisherName,
            productInfo.Description,
            productInfo.Rating,
            productInfo.RatingCount,
            productInfo.Size
        );

        SetLoading(false);
        UpdateInstallButtonState();
    }

    private async Task FetchAndLoadProductAsync(string productId)
    {
        SetLoading(true);

        var result = await StoreEdgeFDProduct.GetProductAsync(
            productId,
            DeviceFamily.Desktop,
            Market.US,
            Lang.en
        );

        if (result.IsSuccess)
        {
            var downloadItem = DownloadManagerService.Instance.GetDownload(productId);
            if (downloadItem != null)
            {
                downloadItem.ProductInfo = result.Value;
            }

            LoadProduct(result.Value);
        }
        else
        {
            SetLoading(false);
            await ShowErrorDialogAsync(
                "Error loading app",
                $"Could not load app details: {result.Exception?.Message}"
            );
        }
    }

    private void SetLoading(bool isLoading)
    {
        LoadingOverlay.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
        DisplayItem.Visibility = isLoading ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateInstallButtonState()
    {
        if (_currentProductInfo == null)
            return;

        var productId = _currentProductInfo.ProductId;
        var downloadManager = DownloadManagerService.Instance;
        var downloadItem = downloadManager.GetDownload(productId);

        if (downloadManager.IsDownloaded(productId))
        {
            SetInstallButtonState(content: "Installed", enabled: false, showProgress: false);
        }
        else if (downloadManager.HasActiveDownload(productId))
        {
            SetInstallButtonState(showProgress: true);
            if (downloadItem != null)
            {
                // Bind to the active download item for progress updates
                _activeDownloadItem = downloadItem;
                BindToDownloadItem(downloadItem);
            }
        }
        else if (downloadItem is { Status: DownloadStatus.Cancelled or DownloadStatus.Failed })
        {
            SetInstallButtonState(content: "Retry", enabled: true, showProgress: false);
        }
    }

    private void BindToDownloadItem(DownloadItem item)
    {
        // Set initial values
        ProgressBar.Value = item.Progress;
        StatusText.Text = item.StatusText;

        // Keep UpdateService in sync so XAML-bound right-side details/progress show correctly.
        UpdateService.SetProgress(item.Progress);

        // During install, show only percent on the right; during download show percent+size (when available).
        var details = item.Status switch
        {
            DownloadStatus.Downloading => $"{(int)Math.Round(item.Progress)}%{item.ProgressDetailsText}",
            DownloadStatus.Installing => $"{(int)Math.Round(item.Progress)}%",
            _ => string.Empty,
        };
        UpdateService.SetDetails(details);

        // Subscribe to property changes
        item.PropertyChanged += OnDownloadItemPropertyChanged;
    }

    private void UnbindFromDownloadItem()
    {
        if (_activeDownloadItem != null)
        {
            _activeDownloadItem.PropertyChanged -= OnDownloadItemPropertyChanged;
            _activeDownloadItem = null;
        }
    }

    private void OnDownloadItemPropertyChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs e
    )
    {
        if (sender is not DownloadItem item)
            return;

        DispatcherQueue.TryEnqueue(() =>
        {
            switch (e.PropertyName)
            {
                case nameof(DownloadItem.Progress):
                    ProgressBar.Value = item.Progress;
                    UpdateService.SetProgress(item.Progress);

                    // Keep details string in sync with progress
                    UpdateService.SetDetails(item.Status switch
                    {
                        DownloadStatus.Downloading => $"{(int)Math.Round(item.Progress)}%{item.ProgressDetailsText}",
                        DownloadStatus.Installing => $"{(int)Math.Round(item.Progress)}%",
                        _ => string.Empty,
                    });
                    break;

                case nameof(DownloadItem.ProgressDetailsText):
                    // Bytes changed: update right-side details for download phase.
                    if (item.Status == DownloadStatus.Downloading)
                    {
                        UpdateService.SetDetails($"{(int)Math.Round(item.Progress)}%{item.ProgressDetailsText}");
                    }
                    break;

                case nameof(DownloadItem.StatusText):
                    // Only update StatusText if the animation is NOT running.
                    // While animation runs, UIUpdateService controls the status line.
                    if (!UpdateService.IsStatusAnimationRunning)
                    {
                        StatusText.Text = item.StatusText;
                    }
                    break;

                case nameof(DownloadItem.Status):
                    // Clear details when switching phases; DownloadHelper will drive AppPage details during active ops.
                    if (item.Status == DownloadStatus.Installing)
                    {
                        UpdateService.SetDetails($"{(int)Math.Round(item.Progress)}%");
                    }
                    else if (item.Status != DownloadStatus.Downloading)
                    {
                        UpdateService.SetDetails(string.Empty);
                    }

                    if (item.Status == DownloadStatus.Completed)
                    {
                        UnbindFromDownloadItem();
                        SetInstallButtonState(
                            content: "Installed",
                            enabled: false,
                            showProgress: false
                        );
                    }
                    else if (item.Status is DownloadStatus.Cancelled or DownloadStatus.Failed)
                    {
                        UnbindFromDownloadItem();
                        SetInstallButtonState(content: "Retry", enabled: true, showProgress: false);
                    }
                    break;
            }
        });
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        UnbindFromDownloadItem();
    }

    private void SetInstallButtonState(
        string content = "Install",
        bool enabled = true,
        bool showProgress = false
    )
    {
        InstallButton.Content = content;
        InstallButton.IsEnabled = enabled;
        InstallButton.Visibility = showProgress ? Visibility.Collapsed : Visibility.Visible;
        ProgressSection.Visibility = showProgress ? Visibility.Visible : Visibility.Collapsed;

        InstallButton.Background = enabled
            ? (Microsoft.UI.Xaml.Media.Brush)
                Application.Current.Resources["AccentFillColorDefaultBrush"]
            : (Microsoft.UI.Xaml.Media.Brush)
                Application.Current.Resources["ControlFillColorDisabledBrush"];
    }

    private async Task ShowErrorDialogAsync(string title, string content)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            CloseButtonText = "OK",
            XamlRoot = this.Content.XamlRoot,
        };
        await dialog.ShowAsync();
    }

    private void LeftArrowButton_Click(object sender, RoutedEventArgs e)
    {
        double offset = ScreenshotsScrollViewer.HorizontalOffset - 654;
        ScreenshotsScrollViewer.ChangeView(Math.Max(0, offset), null, null);
    }

    private void RightArrowButton_Click(object sender, RoutedEventArgs e)
    {
        double offset = ScreenshotsScrollViewer.HorizontalOffset + 654;
        ScreenshotsScrollViewer.ChangeView(
            Math.Min(ScreenshotsScrollViewer.ScrollableWidth, offset),
            null,
            null
        );
    }

    private static bool IsInstallablePackage(string path)
    {
        var ext = Path.GetExtension(path);
        return InstallableExtensions.Any(e => ext.Equals(e, StringComparison.OrdinalIgnoreCase));
    }

    private static string? PickMainPackage(IEnumerable<string> paths)
    {
        // Prefer bundles first, then single packages.
        var list = paths.Where(IsInstallablePackage).ToList();
        return list.OrderByDescending(p =>
                p.EndsWith(".msixbundle", StringComparison.OrdinalIgnoreCase)
            )
            .ThenByDescending(p => p.EndsWith(".appxbundle", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(p => p.EndsWith(".msix", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(p => p.EndsWith(".appx", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProductInfo == null)
            return;

        var productId = _currentProductInfo.ProductId;
        var downloadManager = DownloadManagerService.Instance;

        try
        {
            SetInstallButtonState(showProgress: true);

            var existingDownload = downloadManager.GetDownload(productId);
            if (existingDownload is { Status: DownloadStatus.Cancelled or DownloadStatus.Failed })
            {
                downloadManager.RemoveDownload(productId);
            }

            downloadManager.AddDownload(_currentProductInfo);

            // Bind to the new download item
            var downloadItem = downloadManager.GetDownload(productId);
            if (downloadItem != null)
            {
                _activeDownloadItem = downloadItem;
                BindToDownloadItem(downloadItem);
            }

            // Always use a fresh CTS per attempt
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            StopButton.IsEnabled = true;

            downloadManager.RegisterCancellationToken(productId, _cts);

            // If cancellation was requested from the Downloads page before we got here,
            // stop early.
            if (downloadManager.IsCancellationRequested(productId) || _cts.Token.IsCancellationRequested)
            {
                HandleDownloadError(productId, "Operation canceled.", DownloadStatus.Cancelled);
                return;
            }

            // Clear any leftover details from previous attempts
            UpdateService.SetDetails(string.Empty);
            UpdateService.SetProgress(0);

            // Show fetch phase on both AppPage and DownloadsPage
            UpdateService.StartStatusAnimation("Fetching download URLs");
            downloadManager.UpdateDownloadStatus(productId, DownloadStatus.Pending);
            downloadManager.UpdateDownloadProgress(productId, 0);
            downloadManager.UpdateDownloadStatusText(productId, "Fetching download URLs");

            // Smooth dots animation in Downloads list during fetch.
            var fetchAnimator = new test.Helpers.DownloadItemStatusAnimator(UpdateService.DispatcherQueue);
            if (downloadItem != null)
            {
                fetchAnimator.Start(downloadItem, "Fetching download URLs");
            }

            FileEntry? urls;
            try
            {
                // Ensure the fetch respects cancellation too
                urls = await GetDownloadUrl.fetch(productId, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                UpdateService.StopStatusAnimation();
                if (downloadItem != null)
                {
                    fetchAnimator.Stop(downloadItem);
                }
                HandleDownloadError(productId, "Operation canceled.", DownloadStatus.Cancelled);
                return;
            }

            // If cancelled during fetch without throwing (e.g. external cancellation request)
            if (downloadManager.IsCancellationRequested(productId) || _cts.Token.IsCancellationRequested)
            {
                UpdateService.StopStatusAnimation();
                if (downloadItem != null)
                {
                    fetchAnimator.Stop(downloadItem);
                }
                HandleDownloadError(productId, "Operation canceled.", DownloadStatus.Cancelled);
                return;
            }

            if (urls == null)
            {
                UpdateService.StopStatusAnimation();
                if (downloadItem != null)
                {
                    fetchAnimator.Stop(downloadItem);
                }
                downloadManager.UpdateDownloadStatus(productId, DownloadStatus.Failed);
                UnbindFromDownloadItem();
                SetInstallButtonState(content: "Retry", enabled: true, showProgress: false);
                await ShowErrorDialogAsync(
                    "App not supported",
                    "This app isn't supported. Try a different app or check again later"
                );
                return;
            }

            // DownloadHelper manages animation internally; stop current animation first
            UpdateService.StopStatusAnimation();
            if (downloadItem != null)
            {
                fetchAnimator.Stop(downloadItem);
            }

            await DownloadHelper.StartDownloadAsync(urls, productId, _cts.Token, UpdateService);

            downloadManager.UnregisterCancellationToken(productId);

            if (downloadManager.IsCancellationRequested(productId) || _cts.Token.IsCancellationRequested)
            {
                UnbindFromDownloadItem();
                SetInstallButtonState(content: "Retry", enabled: true, showProgress: false);
                return;
            }

            var downloadedPaths = downloadManager.GetDownload(productId)?.DownloadedFilePaths ?? [];
            var mainPackage = PickMainPackage(downloadedPaths);
            var dependencyPackages = downloadedPaths
                .Where(p => !string.Equals(p, mainPackage, StringComparison.OrdinalIgnoreCase))
                .Where(IsInstallablePackage)
                .ToList();

            if (string.IsNullOrWhiteSpace(mainPackage))
            {
                downloadManager.UpdateDownloadStatus(productId, DownloadStatus.Failed);
                UnbindFromDownloadItem();
                SetInstallButtonState(content: "Retry", enabled: true, showProgress: false);
                await ShowErrorDialogAsync(
                    "Installation failed",
                    "No installable package was found in the downloaded files."
                );
                return;
            }

            // Start installing - fresh animation with no leftover status
            UpdateService.StopStatusAnimation();
            UpdateService.StartStatusAnimation("Installing");

            var progress = new Progress<AppPackageInstaller.InstallProgress>(p =>
            {
                ProgressBar.Value = Math.Clamp(p.Percent, 0, 100);
            });

            try
            {
                await AppPackageInstaller.InstallAsync(
                    mainPackage,
                    dependencyPackages,
                    progress,
                    _cts.Token
                );
            }
            catch (OperationCanceledException)
            {
                UpdateService.StopStatusAnimation();
                HandleDownloadError(productId, "Operation canceled.", DownloadStatus.Cancelled);
                return;
            }
            catch (COMException cex)
            {
                UpdateService.StopStatusAnimation();
                HandleDownloadError(productId, "Failed to install.", DownloadStatus.Failed);
                await InstallHelper.ShowInstallationErrorDialogAsync(
                    this.Content.XamlRoot,
                    "Installation failed",
                    cex
                );
                return;
            }
            catch (UnauthorizedAccessException ua)
            {
                UpdateService.StopStatusAnimation();
                HandleDownloadError(productId, "Failed to install.", DownloadStatus.Failed);
                await InstallHelper.ShowInstallationErrorDialogAsync(
                    this.Content.XamlRoot,
                    "Installation failed",
                    ua
                );
                return;
            }

            UpdateService.StopStatusAnimation();

            // Only mark as completed/installed after installation succeeds.
            downloadManager.UpdateDownloadStatus(productId, DownloadStatus.Completed);

            UnbindFromDownloadItem();
            SetInstallButtonState(content: "Installed", enabled: false, showProgress: false);
        }
        catch (OperationCanceledException)
        {
            UpdateService.StopStatusAnimation();
            HandleDownloadError(productId, "Operation canceled.", DownloadStatus.Cancelled);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            UpdateService.StopStatusAnimation();
            HandleDownloadError(productId, "Failed to install.", DownloadStatus.Failed);
        }
    }

    private void HandleDownloadError(string productId, string status, DownloadStatus downloadStatus)
    {
        StatusText.Text = status;
        DownloadManagerService.Instance.UpdateDownloadStatus(productId, downloadStatus);
        DownloadManagerService.Instance.UnregisterCancellationToken(productId);
        UnbindFromDownloadItem();
        SetInstallButtonState(content: "Retry", enabled: true, showProgress: false);
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProductInfo == null)
            return;

        var productId = _currentProductInfo.ProductId;

        // Show cancelling animation on the main status line
        UpdateService.StartStatusAnimation("Cancelling");

        // Cancel via manager so it works consistently across pages/phases.
        DownloadManagerService.Instance.CancelDownload(productId);

        StopButton.IsEnabled = false;
    }
}
