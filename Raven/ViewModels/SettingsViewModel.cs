using System.Globalization;
using System.Reflection;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Raven.Contracts.Services;
using Raven.Helpers;
using Raven.Services;
using StoreListings.Library;
using Windows.ApplicationModel;

namespace Raven.ViewModels;

public partial class SettingsViewModel : ObservableRecipient
{
    private readonly IThemeSelectorService _themeSelectorService;
    private readonly ILocaleService _localeService;
    private bool _isInitialized;

    [ObservableProperty]
    private ElementTheme _elementTheme;

    [ObservableProperty]
    private string _versionDescription;

    [ObservableProperty]
    private int _selectedMarketIndex;

    [ObservableProperty]
    private int _selectedLanguageIndex;

    private readonly List<(string DisplayName, Market Value)> _marketItems;
    private readonly List<(string DisplayName, Lang Value)> _languageItems;

    public IReadOnlyList<string> AllMarketNames { get; }
    public IReadOnlyList<string> AllLanguageNames { get; }

    public ICommand SwitchThemeCommand { get; }

    public SettingsViewModel(
        IThemeSelectorService themeSelectorService,
        ILocaleService localeService
    )
    {
        _themeSelectorService = themeSelectorService;
        _localeService = localeService;
        _elementTheme = _themeSelectorService.Theme;
        _versionDescription = GetVersionDescription();

        _marketItems = Enum.GetValues<Market>()
            .Select(m => (GetMarketDisplayName(m), m))
            .OrderBy(x => x.Item1, StringComparer.OrdinalIgnoreCase)
            .ToList();
        AllMarketNames = _marketItems.Select(x => x.DisplayName).ToList();
        _selectedMarketIndex = Math.Max(
            0,
            _marketItems.FindIndex(x => x.Value == _localeService.Market)
        );

        _languageItems = Enum.GetValues<Lang>()
            .Select(l => (GetLanguageDisplayName(l), l))
            .OrderBy(x => x.Item1, StringComparer.OrdinalIgnoreCase)
            .ToList();
        AllLanguageNames = _languageItems.Select(x => x.DisplayName).ToList();
        _selectedLanguageIndex = Math.Max(
            0,
            _languageItems.FindIndex(x => x.Value == _localeService.Language)
        );

        SwitchThemeCommand = new RelayCommand<ElementTheme>(
            async (param) =>
            {
                if (ElementTheme != param)
                {
                    ElementTheme = param;
                    await _themeSelectorService.SetThemeAsync(param);
                }
            }
        );

        _isInitialized = true;
    }

    partial void OnSelectedMarketIndexChanged(int value)
    {
        if (!_isInitialized || value < 0 || value >= _marketItems.Count)
            return;
        var market = _marketItems[value].Value;
        if (market != _localeService.Market)
            _ = _localeService.SetMarketAsync(market);
    }

    partial void OnSelectedLanguageIndexChanged(int value)
    {
        if (!_isInitialized || value < 0 || value >= _languageItems.Count)
            return;
        var lang = _languageItems[value].Value;
        if (lang != _localeService.Language)
            _ = _localeService.SetLanguageAsync(lang);
    }

    private static string GetMarketDisplayName(Market market)
    {
        try
        {
            return new RegionInfo(market.ToString()).EnglishName;
        }
        catch
        {
            return market.ToString();
        }
    }

    private static string GetLanguageDisplayName(Lang lang)
    {
        try
        {
            return new CultureInfo(lang.ToString()).EnglishName;
        }
        catch
        {
            return lang.ToString();
        }
    }

    public async Task ResetAppToDefaultAsync()
    {
        DownloadManagerService.Instance.ResetAllDownloads(deleteFiles: true);

        await _themeSelectorService.SetThemeAsync(ElementTheme.Default);
        ElementTheme = _themeSelectorService.Theme;

        await _localeService.ResetToDefaultAsync();

        SelectedMarketIndex = Math.Max(
            0,
            _marketItems.FindIndex(x => x.Value == _localeService.Market)
        );
        SelectedLanguageIndex = Math.Max(
            0,
            _languageItems.FindIndex(x => x.Value == _localeService.Language)
        );
    }

    private static string GetVersionDescription()
    {
        System.Version version = Assembly.GetExecutingAssembly().GetName().Version!;

        return $"{"AppDisplayName".GetLocalized()} - {version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
    }
}
