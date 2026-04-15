using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Raven.Contracts.Services;
using Raven.Helpers;
using Raven.Views.Dialogs;

namespace Raven.Services;

public sealed class AppUpdatePromptService
{
    private const string CheckUpdatesOnStartupKey = "CheckForAppUpdatesOnStartup";
    private const string RuntimeLoggerCategory = "Raven.Runtime";
    private const string DefaultCheckForUpdatesLabel = "Check for updates";

    private readonly GitHubUpdaterService _gitHubUpdaterService;
    private readonly ILocalSettingsService _localSettingsService;
    private readonly ILogger _logger;

    public AppUpdatePromptService(
        GitHubUpdaterService gitHubUpdaterService,
        ILocalSettingsService localSettingsService,
        ILoggerFactory loggerFactory
    )
    {
        _gitHubUpdaterService = gitHubUpdaterService;
        _localSettingsService = localSettingsService;
        _logger = loggerFactory.CreateLogger(RuntimeLoggerCategory);
    }

    public async Task ShowManualUpdateDialogAsync(
        XamlRoot xamlRoot,
        CancellationToken cancellationToken = default,
        bool startCheckImmediately = true,
        string? initialTitle = null,
        string? initialStatusMessage = null
    )
    {
        var dialogContent = new UpdateDialogContent();
        var checkOnStartup = dialogContent.StartupPreferenceCheckBoxControl;
        checkOnStartup.IsChecked = await GetCheckOnStartupEnabledAsync();

        var messageTextBlock = dialogContent.StatusMessageTextBlockControl;
        var progressBar = dialogContent.StatusProgressBarControl;
        var progressText = dialogContent.StatusProgressTextBlockControl;

        var closeLabel = "Settings_UpdaterDialogClose".GetLocalized();
        var cancelLabel = "Settings_UpdaterDialogCancel".GetLocalized();
        var checkForUpdatesLabel = GetLocalizedOrDefault(
            "Settings_AppUpdatesButton.Content",
            DefaultCheckForUpdatesLabel
        );

        var dialog = new ContentDialog
        {
            Content = dialogContent,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot,
        };

        using var statusAnimation = new StatusAnimationController(
            dialogContent,
            messageTextBlock
        );

        var hasPersistedStartupPreference = false;
        var isChecking = false;
        var isDownloading = false;
        var isClosing = false;
        GitHubReleaseInfo? availableRelease = null;
        CancellationTokenSource? checkCts = null;
        CancellationTokenSource? downloadCts = null;

        void SetCheckActionState(string? messageOverride = null)
        {
            statusAnimation.Stop(
                messageOverride ?? "Settings_UpdaterCheckCanceledContent".GetLocalized()
            );

            SetStartupPreferenceVisibility(checkOnStartup, isVisible: true);
            ResetProgressUi(progressBar, progressText);
            SetDialogButtons(
                dialog,
                primaryText: checkForUpdatesLabel,
                isPrimaryEnabled: true,
                closeText: closeLabel
            );
        }

        void SetUpdateAvailableState(GitHubReleaseInfo release)
        {
            var latestLabel = release.LatestVersion?.ToString() ?? release.TagName;
            dialog.Title = "Settings_UpdaterConfirmTitle".GetLocalized();
            statusAnimation.Stop(
                string.Format("Settings_UpdaterConfirmContent".GetLocalized(), latestLabel)
            );

            SetStartupPreferenceVisibility(checkOnStartup, isVisible: true);
            ResetProgressUi(progressBar, progressText);
            SetDialogButtons(
                dialog,
                primaryText: "Settings_UpdaterConfirmPrimary".GetLocalized(),
                isPrimaryEnabled: true,
                closeText: closeLabel
            );
        }

        async Task StartManualCheckAsync()
        {
            checkCts = ReplaceTokenSource(checkCts, cancellationToken);
            availableRelease = null;
            isChecking = true;

            _logger.LogInformation("Update check started");

            dialog.Title = "Settings_UpdaterCheckingTitle".GetLocalized();
            statusAnimation.Start("Settings_UpdaterCheckingContent".GetLocalized());

            SetStartupPreferenceVisibility(checkOnStartup, isVisible: false);
            ShowCheckingProgress(progressBar, progressText);
            SetDialogButtons(
                dialog,
                primaryText: checkForUpdatesLabel,
                isPrimaryEnabled: false,
                closeText: cancelLabel
            );

            try
            {
                var release = await _gitHubUpdaterService.GetLatestReleaseAsync(checkCts.Token);
                isChecking = false;

                if (release.IsUpToDate)
                {
                    _logger.LogInformation(
                        "Already on latest version: {CurrentVersion}",
                        release.LatestVersion?.ToString() ?? release.TagName
                    );
                    dialog.Title = "Settings_UpdaterAlreadyLatestTitle".GetLocalized();
                    SetCheckActionState("Settings_UpdaterAlreadyLatestContent".GetLocalized());
                    return;
                }

                availableRelease = release;
                _logger.LogInformation(
                    "Update available: version {LatestVersion}",
                    release.LatestVersion?.ToString() ?? release.TagName
                );
                SetUpdateAvailableState(release);
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                isChecking = false;
                _logger.LogInformation("Update check canceled by user");

                if (!isClosing)
                {
                    SetCheckActionState();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Update check failed");
                isChecking = false;

                dialog.Title = "Settings_UpdaterErrorTitle".GetLocalized();
                statusAnimation.Stop(
                    string.Format("Settings_UpdaterErrorContent".GetLocalized(), ex.Message)
                );

                SetStartupPreferenceVisibility(checkOnStartup, isVisible: true);
                ResetProgressUi(progressBar, progressText);
                SetDialogButtons(
                    dialog,
                    primaryText: checkForUpdatesLabel,
                    isPrimaryEnabled: true,
                    closeText: closeLabel
                );
            }
        }

        async Task StartManualUpdateAsync()
        {
            if (availableRelease is null)
            {
                return;
            }

            try
            {
                await PersistStartupPreferenceAsync(checkOnStartup);
                hasPersistedStartupPreference = true;

                isDownloading = true;
                downloadCts = ReplaceTokenSource(downloadCts, cancellationToken);

                _logger.LogInformation(
                    "Update installation started: version {Version}",
                    availableRelease.LatestVersion?.ToString() ?? availableRelease.TagName
                );

                ShowInstallingProgress(progressBar, progressText);
                SetStartupPreferenceVisibility(checkOnStartup, isVisible: false);
                SetDialogButtons(
                    dialog,
                    primaryText: dialog.PrimaryButtonText,
                    isPrimaryEnabled: false,
                    closeText: cancelLabel
                );

                dialog.Title = "Settings_UpdaterInstallingTitle".GetLocalized();
                statusAnimation.Start("Status_Installing".GetLocalized());

                var progress = CreateProgressReporter(progressBar, progressText);
                await _gitHubUpdaterService.StartUpdateAsync(
                    availableRelease,
                    progress,
                    downloadCts.Token
                );
                Application.Current.Exit();
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                isDownloading = false;
                downloadCts?.Dispose();
                downloadCts = null;
                _logger.LogInformation("Update installation canceled by user");

                ResetProgressUi(progressBar, progressText);

                if (availableRelease is not null)
                {
                    SetUpdateAvailableState(availableRelease);
                }
                else
                {
                    SetCheckActionState();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Update installation failed");

                isDownloading = false;
                downloadCts?.Dispose();
                downloadCts = null;

                dialog.Title = "Settings_UpdaterErrorTitle".GetLocalized();
                statusAnimation.Stop(
                    string.Format("Settings_UpdaterErrorContent".GetLocalized(), ex.Message)
                );

                SetStartupPreferenceVisibility(checkOnStartup, isVisible: true);
                ResetProgressUi(progressBar, progressText);
                SetDialogButtons(
                    dialog,
                    primaryText: checkForUpdatesLabel,
                    isPrimaryEnabled: true,
                    closeText: closeLabel
                );
                availableRelease = null;
            }
        }

        dialog.Closing += (_, args) =>
        {
            if (isDownloading)
            {
                args.Cancel = true;
                downloadCts?.Cancel();
                return;
            }

            isClosing = true;
            downloadCts?.Cancel();
            statusAnimation.Stop();
        };

        dialog.PrimaryButtonClick += (_, args) =>
        {
            args.Cancel = true;

            if (isChecking || isDownloading)
            {
                return;
            }

            if (availableRelease is null)
            {
                StartManualCheckAsync();
                return;
            }

            StartManualUpdateAsync();
        };

        Task? checkTask = null;
        if (startCheckImmediately)
        {
            checkTask = StartManualCheckAsync();
        }
        else
        {
            dialog.Title = initialTitle ?? "Settings_UpdaterCheckingTitle".GetLocalized();
            SetCheckActionState(initialStatusMessage);
        }

        await dialog.ShowAsync();

        isClosing = true;
        checkCts?.Cancel();
        downloadCts?.Cancel();

        if (checkTask is not null)
        {
            try
            {
                await checkTask;
            }
            catch
            {
            }
        }

        checkCts?.Dispose();
        downloadCts?.Dispose();

        if (!hasPersistedStartupPreference)
        {
            await PersistStartupPreferenceAsync(checkOnStartup);
        }
    }

    public async Task CheckForUpdatesOnStartupAsync(
        XamlRoot xamlRoot,
        CancellationToken cancellationToken = default
    )
    {
        var shouldCheck = await GetCheckOnStartupEnabledAsync();
        if (!shouldCheck)
        {
            return;
        }

        try
        {
            var release = await _gitHubUpdaterService.GetLatestReleaseAsync(cancellationToken);
            if (release.IsUpToDate)
            {
                return;
            }

            _logger.LogInformation(
                "Update available on startup: version {Version}",
                release.LatestVersion?.ToString() ?? release.TagName
            );
            await ShowAvailableUpdateDialogAsync(release, xamlRoot, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Startup update check failed");

            await ShowManualUpdateDialogAsync(
                xamlRoot,
                cancellationToken,
                startCheckImmediately: false,
                initialTitle: "Settings_UpdaterErrorTitle".GetLocalized(),
                initialStatusMessage: string.Format(
                    "Settings_UpdaterErrorContent".GetLocalized(),
                    ex.Message
                )
            );
        }
    }

    private async Task ShowAvailableUpdateDialogAsync(
        GitHubReleaseInfo release,
        XamlRoot xamlRoot,
        CancellationToken cancellationToken
    )
    {
        var latestLabel = release.LatestVersion?.ToString() ?? release.TagName;
        var confirmMessage = string.Format(
            "Settings_UpdaterConfirmContent".GetLocalized(),
            latestLabel
        );

        var dialogContent = new UpdateDialogContent();
        var messageTextBlock = dialogContent.StatusMessageTextBlockControl;
        var checkOnStartup = dialogContent.StartupPreferenceCheckBoxControl;
        var progressBar = dialogContent.StatusProgressBarControl;
        var progressText = dialogContent.StatusProgressTextBlockControl;

        messageTextBlock.Text = confirmMessage;
        checkOnStartup.IsChecked = await GetCheckOnStartupEnabledAsync();

        var closeLabel = "Settings_UpdaterDialogClose".GetLocalized();
        var cancelLabel = "Settings_UpdaterDialogCancel".GetLocalized();

        var dialog = new ContentDialog
        {
            Title = "Settings_UpdaterConfirmTitle".GetLocalized(),
            Content = dialogContent,
            PrimaryButtonText = "Settings_UpdaterConfirmPrimary".GetLocalized(),
            CloseButtonText = closeLabel,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot,
        };

        using var statusAnimation = new StatusAnimationController(
            dialogContent,
            messageTextBlock
        );

        var hasPersistedStartupPreference = false;
        var isDownloading = false;
        CancellationTokenSource? downloadCts = null;

        async Task StartStartupUpdateAsync()
        {
            try
            {
                await PersistStartupPreferenceAsync(checkOnStartup);
                hasPersistedStartupPreference = true;

                isDownloading = true;
                downloadCts = ReplaceTokenSource(downloadCts, cancellationToken);

                _logger.LogInformation(
                    "Update installation started (startup): version {Version}",
                    release.LatestVersion?.ToString() ?? release.TagName
                );

                ShowInstallingProgress(progressBar, progressText);
                SetStartupPreferenceVisibility(checkOnStartup, isVisible: false);
                SetDialogButtons(
                    dialog,
                    primaryText: dialog.PrimaryButtonText,
                    isPrimaryEnabled: false,
                    closeText: cancelLabel
                );

                dialog.Title = "Settings_UpdaterInstallingTitle".GetLocalized();
                statusAnimation.Start("Status_Installing".GetLocalized());

                var progress = CreateProgressReporter(progressBar, progressText);
                await _gitHubUpdaterService.StartUpdateAsync(release, progress, downloadCts.Token);
                Application.Current.Exit();
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                isDownloading = false;
                downloadCts?.Dispose();
                downloadCts = null;
                _logger.LogInformation("Update installation canceled by user (startup)");

                ResetProgressUi(progressBar, progressText);
                SetStartupPreferenceVisibility(checkOnStartup, isVisible: true);
                SetDialogButtons(
                    dialog,
                    primaryText: dialog.PrimaryButtonText,
                    isPrimaryEnabled: true,
                    closeText: closeLabel
                );
                statusAnimation.Stop(confirmMessage);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Update installation failed");

                isDownloading = false;
                downloadCts?.Dispose();
                downloadCts = null;

                dialog.Title = "Settings_UpdaterErrorTitle".GetLocalized();
                statusAnimation.Stop(
                    string.Format("Settings_UpdaterErrorContent".GetLocalized(), ex.Message)
                );

                SetStartupPreferenceVisibility(checkOnStartup, isVisible: true);
                ResetProgressUi(progressBar, progressText);
                SetDialogButtons(
                    dialog,
                    primaryText: dialog.PrimaryButtonText,
                    isPrimaryEnabled: true,
                    closeText: closeLabel
                );
            }
        }

        dialog.Closing += (_, args) =>
        {
            if (isDownloading)
            {
                args.Cancel = true;
                downloadCts?.Cancel();
                return;
            }

            statusAnimation.Stop();
        };

        dialog.PrimaryButtonClick += (_, args) =>
        {
            args.Cancel = true;

            if (isDownloading)
            {
                return;
            }

            StartStartupUpdateAsync();
        };

        await dialog.ShowAsync();

        downloadCts?.Cancel();
        downloadCts?.Dispose();

        if (!hasPersistedStartupPreference)
        {
            await PersistStartupPreferenceAsync(checkOnStartup);
        }
    }

    private static string GetLocalizedOrDefault(string key, string fallback)
    {
        var localized = key.GetLocalized();
        return string.IsNullOrWhiteSpace(localized) ? fallback : localized;
    }

    private static CancellationTokenSource ReplaceTokenSource(
        CancellationTokenSource? existing,
        CancellationToken cancellationToken
    )
    {
        existing?.Cancel();
        existing?.Dispose();
        return CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    }

    private static void SetStartupPreferenceVisibility(CheckBox checkOnStartup, bool isVisible)
    {
        checkOnStartup.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
        checkOnStartup.IsEnabled = isVisible;
    }

    private static void ShowCheckingProgress(ProgressBar progressBar, TextBlock progressText)
    {
        progressBar.IsIndeterminate = true;
        progressBar.Value = 0;
        progressBar.Visibility = Visibility.Visible;
        progressText.Visibility = Visibility.Collapsed;
    }

    private static void ShowInstallingProgress(ProgressBar progressBar, TextBlock progressText)
    {
        progressBar.IsIndeterminate = false;
        progressBar.Value = 0;
        progressBar.Visibility = Visibility.Visible;
        progressText.Visibility = Visibility.Visible;
        progressText.Text = "0%";
    }

    private static void ResetProgressUi(ProgressBar progressBar, TextBlock progressText)
    {
        progressBar.Visibility = Visibility.Collapsed;
        progressText.Visibility = Visibility.Collapsed;
        progressBar.Value = 0;
    }

    private static void SetDialogButtons(
        ContentDialog dialog,
        string primaryText,
        bool isPrimaryEnabled,
        string closeText
    )
    {
        dialog.PrimaryButtonText = primaryText;
        dialog.IsPrimaryButtonEnabled = isPrimaryEnabled;
        dialog.IsSecondaryButtonEnabled = true;
        dialog.CloseButtonText = closeText;
    }

    private static IProgress<double> CreateProgressReporter(
        ProgressBar progressBar,
        TextBlock progressText
    )
    {
        var format = "Settings_UpdaterProgressPercent".GetLocalized();
        return new Progress<double>(value =>
        {
            var percent = (int)Math.Round(value * 100);
            progressBar.Value = percent;
            progressText.Text = string.Format(format, percent);
        });
    }

    private async Task PersistStartupPreferenceAsync(CheckBox checkOnStartup)
    {
        var enabled = checkOnStartup.IsChecked != false;
        await _localSettingsService.SaveSettingAsync(CheckUpdatesOnStartupKey, enabled);
    }

    private async Task<bool> GetCheckOnStartupEnabledAsync()
    {
        try
        {
            var value = await _localSettingsService.ReadSettingAsync<bool?>(CheckUpdatesOnStartupKey);
            if (value.HasValue)
            {
                return value.Value;
            }

            await _localSettingsService.SaveSettingAsync(CheckUpdatesOnStartupKey, true);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read startup check setting");
            return true;
        }
    }

    private sealed class StatusAnimationController : IDisposable
    {
        private readonly UIUpdateService _statusAnimationService;
        private readonly TextBlock _messageTextBlock;

        public StatusAnimationController(UpdateDialogContent dialogContent, TextBlock messageTextBlock)
        {
            _messageTextBlock = messageTextBlock;
            _statusAnimationService = new UIUpdateService(dialogContent.DispatcherQueue);
            _statusAnimationService.PropertyChanged += OnStatusAnimationPropertyChanged;
        }

        public void Start(string baseText)
        {
            var normalized = baseText.TrimEnd(' ', '.', '…');
            _statusAnimationService.StartStatusAnimation(
                string.IsNullOrWhiteSpace(normalized) ? baseText : normalized
            );
        }

        public void Stop(string? message = null)
        {
            _statusAnimationService.StopStatusAnimation();
            if (message is not null)
            {
                _messageTextBlock.Text = message;
            }
        }

        public void Dispose()
        {
            _statusAnimationService.StopStatusAnimation();
            _statusAnimationService.PropertyChanged -= OnStatusAnimationPropertyChanged;
        }

        private void OnStatusAnimationPropertyChanged(
            object? sender,
            System.ComponentModel.PropertyChangedEventArgs e
        )
        {
            if (e.PropertyName == nameof(UIUpdateService.StatusText))
            {
                _messageTextBlock.Text = _statusAnimationService.StatusText;
            }
        }
    }
}
