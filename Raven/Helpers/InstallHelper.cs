using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Raven.Helpers;

public static class InstallHelper
{
    private const int ERROR_PACKAGED_SERVICE_REQUIRES_ADMIN = unchecked((int)0x80073D28);
    private const int ERROR_INSTALL_CONFLICTING_PACKAGE = unchecked((int)0x80073D06);

    public static string GetFriendlyMsixError(int hresult, string message)
    {
        const int ERROR_DEPLOYMENT_IN_PROGRESS = unchecked((int)0x80073D01);
        const int ERROR_INVALID_PACKAGE = unchecked((int)0x80073CF3);
        const int ERROR_PACKAGE_NOT_FOUND = unchecked((int)0x80073CFA);
        const int ERROR_DEPLOYMENT_FAILURE = unchecked((int)0x80073CF9);

        return hresult switch
        {
            ERROR_INSTALL_CONFLICTING_PACKAGE =>
                "Install_Error_ConflictingVersion".GetLocalized(),
            ERROR_DEPLOYMENT_IN_PROGRESS =>
                "Install_Error_DeploymentInProgress".GetLocalized(),
            ERROR_INVALID_PACKAGE =>
                "Install_Error_InvalidPackage".GetLocalized(),
            ERROR_PACKAGE_NOT_FOUND =>
                "Install_Error_PackageNotFound".GetLocalized(),
            ERROR_DEPLOYMENT_FAILURE =>
                "Install_Error_DeploymentFailure".GetLocalized(),
            ERROR_PACKAGED_SERVICE_REQUIRES_ADMIN =>
                "Install_Error_AdminRequired".GetLocalized(),
            _ => string.Format("Install_Error_GenericDeployment".GetLocalized(), hresult, message),
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

    public static bool IsNewerOrSameVersionInstalled(int hresult) =>
        hresult == ERROR_INSTALL_CONFLICTING_PACKAGE;

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

        var content = exception switch
        {
            COMException comEx => GetFriendlyMsixError(comEx.HResult, comEx.Message),
            UnauthorizedAccessException ua =>
                string.Format("Install_Error_AccessDenied".GetLocalized(), ua.Message),
            _ => string.Format("Install_Error_Generic".GetLocalized(), exception.Message),
        };

        await ShowDialogAsync(xamlRoot, title, content);
    }

    public static async Task<bool> ShowInstallationErrorOrForceInstallDialogAsync(
        XamlRoot xamlRoot,
        string title,
        Exception exception
    )
    {
        if (exception is COMException comEx && IsNewerOrSameVersionInstalled(comEx.HResult))
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = GetFriendlyMsixError(comEx.HResult, comEx.Message),
                PrimaryButtonText = "Install_Btn_ForceInstall".GetLocalized(),
                CloseButtonText = "Common_OK".GetLocalized(),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = xamlRoot,
            };

            var result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary;
        }
        return false;
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
            PrimaryButtonText = "Install_Btn_RunAsAdmin".GetLocalized(),
            CloseButtonText = "Common_OK".GetLocalized(),
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
            CloseButtonText = "Common_OK".GetLocalized(),
            XamlRoot = xamlRoot,
        };
        await dialog.ShowAsync();
    }
}
