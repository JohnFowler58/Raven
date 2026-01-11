using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace test.Helpers;

public static class InstallHelper
{
    private const int ERROR_PACKAGED_SERVICE_REQUIRES_ADMIN = unchecked((int)0x80073D28);

    public static string GetFriendlyMsixError(int hresult, string message)
    {
        const int ERROR_INSTALL_CONFLICTING_PACKAGE = unchecked((int)0x80073D06);
        const int ERROR_DEPLOYMENT_IN_PROGRESS = unchecked((int)0x80073D01);
        const int ERROR_INVALID_PACKAGE = unchecked((int)0x80073CF3);
        const int ERROR_PACKAGE_NOT_FOUND = unchecked((int)0x80073CFA);
        const int ERROR_DEPLOYMENT_FAILURE = unchecked((int)0x80073CF9);

        return hresult switch
        {
            ERROR_INSTALL_CONFLICTING_PACKAGE =>
                "A newer or the same version is already installed.",
            ERROR_DEPLOYMENT_IN_PROGRESS =>
                "Another installation is in progress. Wait for it to finish and try again.",
            ERROR_INVALID_PACKAGE =>
                "Invalid or unsupported package. Ensure the package and dependencies are supported for your system.",
            ERROR_PACKAGE_NOT_FOUND =>
                "Package not found. Check the selected/downloaded file path.",
            ERROR_DEPLOYMENT_FAILURE =>
                "Windows deployment failed. Check system policies or try again.",
            ERROR_PACKAGED_SERVICE_REQUIRES_ADMIN =>
                "This package includes a packaged service and must be installed with administrator privileges. Relaunch the app as administrator and try again.",
            _ => $"Windows deployment error (0x{hresult:X8}). {message}",
        };
    }

    public static bool IsRunningAsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public static bool IsPackagedServiceAdminRequired(int hresult) =>
        hresult == ERROR_PACKAGED_SERVICE_REQUIRES_ADMIN;

    public static async Task ShowInstallationErrorDialogAsync(
        XamlRoot xamlRoot,
        string title,
        Exception exception
    )
    {
        if (
            exception is COMException cex
            && IsPackagedServiceAdminRequired(cex.HResult)
            && !IsRunningAsAdministrator()
        )
        {
            await ShowAdminRequiredDialogAsync(xamlRoot, title, cex);
            return;
        }

        string content = exception switch
        {
            COMException comEx => GetFriendlyMsixError(comEx.HResult, comEx.Message),
            UnauthorizedAccessException ua =>
                "Failed: Access denied. Try running as administrator or ensure sideloading policy allows app packages. "
                    + ua.Message,
            _ => $"Failed: {exception.Message}",
        };

        await ShowDialogAsync(xamlRoot, title, content);
    }

    private static async Task ShowAdminRequiredDialogAsync(
        XamlRoot xamlRoot,
        string title,
        COMException cex
    )
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = GetFriendlyMsixError(cex.HResult, cex.Message),
            PrimaryButtonText = "Run as administrator",
            CloseButtonText = "OK",
            XamlRoot = xamlRoot,
            DefaultButton = ContentDialogButton.Primary,
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            if (
                ElevationHelper.TryRelaunchAsAdministrator(
                    Environment.GetCommandLineArgs().Skip(1).ToArray()
                )
            )
            {
                // Exit current instance; the elevated instance will continue.
                Environment.Exit(0);
            }
        }
    }

    public static async Task ShowDialogAsync(XamlRoot xamlRoot, string title, string content)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            CloseButtonText = "OK",
            XamlRoot = xamlRoot,
        };
        await dialog.ShowAsync();
    }
}
