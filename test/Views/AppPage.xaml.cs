using System.Diagnostics;
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

    public AppPage()
    {
        ViewModel = App.GetService<AppViewModel>();
        InitializeComponent();
        UpdateService = new UIUpdateService(this.DispatcherQueue);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is StoreEdgeFDProduct ProductInfo)
        {
            LoadData(ProductInfo);
        }
    }

    private void LoadData(StoreEdgeFDProduct ProductInfo)
    {
        {
            DisplayItem.Visibility = Visibility.Collapsed;
            LoadingOverlay.Visibility = Visibility.Visible;
            // add product id to app data
            AppData.SetValues(
                ProductInfo.ProductId,
                ProductInfo.Logo,
                ProductInfo.Screenshots,
                ProductInfo.RevisionId,
                ProductInfo.Title,
                ProductInfo.PublisherName,
                ProductInfo.Description,
                ProductInfo.Rating,
                ProductInfo.RatingCount,
                ProductInfo.Size
            );

            LoadingOverlay.Visibility = Visibility.Collapsed;
            DisplayItem.Visibility = Visibility.Visible;
        }
    }

    private void LeftArrowButton_Click(object sender, RoutedEventArgs e)
    {
        // Scroll left by a fixed amount or to the previous item
        double offset = ScreenshotsScrollViewer.HorizontalOffset - 654; // Width + spacing
        ScreenshotsScrollViewer.ChangeView(Math.Max(0, offset), null, null);
    }

    private void RightArrowButton_Click(object sender, RoutedEventArgs e)
    {
        // Scroll right by a fixed amount or to the next item
        double offset = ScreenshotsScrollViewer.HorizontalOffset + 654; // Width + spacing
        ScreenshotsScrollViewer.ChangeView(
            Math.Min(ScreenshotsScrollViewer.ScrollableWidth, offset),
            null,
            null
        );
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            InstallButton.Visibility = Visibility.Collapsed;
            ProgressSection.Visibility = Visibility.Visible;

            UpdateService.StartStatusAnimation("Fetching download URLs");
            var urls = await GetDownloadUrl.fetch(AppData.ProductID);
            Debug.WriteLine(urls);

            UpdateService.StartStatusAnimation("Preparing download...");

            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            StopButton.IsEnabled = true;
            var reporter = UpdateService.GetReporter();
            await DownloadHelper.StartDownloadAsync(urls, _cts.Token, UpdateService);
        }
        catch (OperationCanceledException)
        {
            UpdateService.SetStatus("Operation canceled.");
            UpdateService.SetDetails("The operation was canceled by the user.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            UpdateService.SetStatus("Failed to install.");
            UpdateService.SetDetails("Please check logs and retry.");
            ProgressSection.Visibility = Visibility.Collapsed;
            InstallButton.Visibility = Visibility.Visible;
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel(); // Signal cancellation
        _cts?.Dispose(); // Then dispose
        StopButton.IsEnabled = false;
    }
}
