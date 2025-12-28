using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using test.Helpers;
using test.Services;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace test.Views;

public sealed partial class InstallationsPage : Page
{
    private string? _selectedPath;
    public UIUpdateService UpdateService { get; }

    public InstallationsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
        UpdateService = new UIUpdateService(this.DispatcherQueue);
        UpdateService.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(UIUpdateService.StatusText))
            {
                ProgressStatusText.Text = UpdateService.StatusText;
            }
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateLayoutForViewport(ActualHeight);
        UpdateDropZoneTypography(ActualWidth);
        // Reset to default content on load
        InstallButton.Content = "Install";
        InstallButton.IsEnabled = !string.IsNullOrWhiteSpace(SelectedFileText.Text);
        ProgressPercentText.Text = string.Empty;
        ProgressStatusText.Text = string.Empty;
        UpdateService.StopStatusAnimation();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateLayoutForViewport(e.NewSize.Height);
        UpdateDropZoneTypography(e.NewSize.Width);
    }

    private void UpdateLayoutForViewport(double viewportHeight)
    {
        var headerAllowance = 200; // approximate title + progress + path row
        var desired = Math.Max(300, (viewportHeight - headerAllowance) * 2.0 / 3.0);
        DropZoneButton.MinHeight = desired;
    }

    private void UpdateDropZoneTypography(double width)
    {
        // Simple responsive sizing based on width tiers
        double iconSize;
        double textSize;
        if (width < 500)
        {
            iconSize = 28;
            textSize = 16;
        }
        else if (width < 900)
        {
            iconSize = 36;
            textSize = 18;
        }
        else
        {
            iconSize = 44;
            textSize = 20;
        }

        DropZoneIcon.FontSize = iconSize;
        DropZoneText.FontSize = textSize;
    }

    private void SetSelectedFile(string? path)
    {
        _selectedPath = path;
        SelectedFileText.Text = string.IsNullOrWhiteSpace(path) ? string.Empty : path;
        // Reset button content to Install on new selection or clearing
        InstallButton.Content = "Install";
        InstallButton.IsEnabled = !string.IsNullOrWhiteSpace(SelectedFileText.Text);
        ClearButton.Visibility = string.IsNullOrWhiteSpace(SelectedFileText.Text)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        SetSelectedFile(null);
        ProgressPanel.Visibility = Visibility.Collapsed;
        InstallProgressBar.Value = 0;
        ProgressPercentText.Text = string.Empty;
        ProgressStatusText.Text = string.Empty;
        UpdateService.StopStatusAnimation();
    }

    private async void DropZoneButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".appx");
        picker.FileTypeFilter.Add(".msix");
        picker.FileTypeFilter.Add(".appxbundle");
        picker.FileTypeFilter.Add(".msixbundle");

        var hwnd = WindowNative.GetWindowHandle(App.MainWindow);
        InitializeWithWindow.Initialize(picker, hwnd);

        StorageFile? file = await picker.PickSingleFileAsync();
        if (file != null)
        {
            SetSelectedFile(file.Path);
        }
    }

    private void DropZone_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Drop to select package";
        e.DragUIOverride.IsCaptionVisible = true;
        e.Handled = true;
    }

    private async void DropZone_Drop(object sender, DragEventArgs e)
    {
        if (
            e.DataView.Contains(
                Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems
            )
        )
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var file = items.OfType<StorageFile>().FirstOrDefault();
            if (file != null)
            {
                var ext = Path.GetExtension(file.Path).ToLowerInvariant();
                if (ext is ".appx" or ".msix" or ".appxbundle" or ".msixbundle")
                {
                    SetSelectedFile(file.Path);
                }
            }
        }
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        var path = SelectedFileText.Text;
        if (string.IsNullOrWhiteSpace(path))
            return;

        await PerformInstallAsync(path);
    }

    private async Task PerformInstallAsync(string path)
    {
        InstallButton.IsEnabled = false;
        ProgressPanel.Visibility = Visibility.Visible;
        InstallProgressBar.Value = 0;
        ProgressPercentText.Text = "0%";
        UpdateService.StartStatusAnimation("Installing");

        var progress = new Progress<AppPackageInstaller.InstallProgress>(p =>
        {
            var percent = Math.Clamp(p.Percent, 0, 100);
            InstallProgressBar.Value = percent;
            ProgressPercentText.Text = $"{percent}%";
            // Leave status animation to UpdateService
        });

        var succeeded = false;
        string? errorMessage = null;
        try
        {
            await AppPackageInstaller.InstallAsync(path, progress);
            UpdateService.StopStatusAnimation();
            ProgressStatusText.Text = "Installed.";
            ProgressPercentText.Text = "100%";
            succeeded = true;
        }
        catch (COMException cex)
        {
            UpdateService.StopStatusAnimation();
            const int ERROR_PACKAGES_IN_USE = unchecked((int)0x80073D02);
            if (cex.HResult == ERROR_PACKAGES_IN_USE)
            {
                await ShowPackagesInUseDialogAsync(path);
                return; // dialog will handle retry flow
            }
            else
            {
                errorMessage = GetFriendlyMsixError(cex.HResult, cex.Message);
                ProgressStatusText.Text = "Error";
            }
        }
        catch (UnauthorizedAccessException ua)
        {
            UpdateService.StopStatusAnimation();
            errorMessage =
                "Failed: Access denied. Try running as administrator or ensure sideloading policy allows app packages. "
                + ua.Message;
            ProgressStatusText.Text = "Error";
        }
        catch (Exception ex)
        {
            UpdateService.StopStatusAnimation();
            errorMessage = $"Failed: {ex.Message}";
            ProgressStatusText.Text = "Error";
        }
        finally
        {
            // Hide progress
            ProgressPanel.Visibility = Visibility.Collapsed;
            InstallProgressBar.Value = 0;
            ProgressPercentText.Text = string.Empty;

            if (succeeded)
            {
                // Show tick and keep disabled until next selection
                InstallButton.Content = new SymbolIcon { Symbol = Symbol.Accept };
                InstallButton.IsEnabled = false;
                // Clear text, but do NOT reset button content
                SelectedFileText.Text = string.Empty;
                ClearButton.Visibility = Visibility.Collapsed;
            }
            else if (!string.IsNullOrWhiteSpace(errorMessage))
            {
                // Show cross icon for failure until next selection
                InstallButton.Content = new SymbolIcon { Symbol = Symbol.Cancel };
                InstallButton.IsEnabled = false;

                await ShowErrorDialogAsync("Installation failed", errorMessage);
            }
        }
    }

    private async Task ShowPackagesInUseDialogAsync(string path)
    {
        var dialog = new ContentDialog
        {
            Title = "App is running",
            Content = "Another app using this package is running. Close it and retry?",
            PrimaryButtonText = "Retry & Kill",
            CloseButtonText = "OK",
            XamlRoot = this.Content.XamlRoot,
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            // try elevated close
            var elevatedClosed = await InstallHelper.TryCloseBlockingProcessesElevatedAsync(path);
            if (!elevatedClosed)
            {
                await ShowErrorDialogAsync(
                    "Retry failed",
                    "Could not close running apps (elevation canceled or failed). Try running the app as administrator and retry."
                );
            }
            // retry regardless (apps might have closed)
            await PerformInstallAsync(path);
        }
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

    private static string GetFriendlyMsixError(int hresult, string message)
    {
        // Common MSIX/AppX deployment HRESULTs
        const int ERROR_PACKAGES_IN_USE = unchecked((int)0x80073D02); // Close apps using package
        const int ERROR_INSTALL_CONFLICTING_PACKAGE = unchecked((int)0x80073D06); // Same or higher version installed
        const int ERROR_DEPLOYMENT_IN_PROGRESS = unchecked((int)0x80073D01); // Another install in progress
        const int ERROR_INVALID_PACKAGE = unchecked((int)0x80073CF3);
        const int ERROR_PACKAGE_NOT_FOUND = unchecked((int)0x80073CFA);
        const int ERROR_DEPLOYMENT_FAILURE = unchecked((int)0x80073CF9);

        return hresult switch
        {
            ERROR_PACKAGES_IN_USE => "Close apps that are using this package and try again.",
            ERROR_INSTALL_CONFLICTING_PACKAGE =>
                "A newer or the same version is already installed.",
            ERROR_DEPLOYMENT_IN_PROGRESS =>
                "Another installation is in progress. Wait for it to finish and try again.",
            ERROR_INVALID_PACKAGE =>
                "Invalid or unsupported package. Ensure the package and dependencies are supported for your system.",
            ERROR_PACKAGE_NOT_FOUND => "Package not found. Check the selected file path.",
            ERROR_DEPLOYMENT_FAILURE =>
                "Windows deployment failed. Check system policies or try again.",
            _ => $"Windows deployment error (0x{hresult:X8}). {message}",
        };
    }
}
