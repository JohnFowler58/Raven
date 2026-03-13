using System.Diagnostics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using StoreListings.Library;
using test.Contracts.Services;
using test.Helpers;
using test.ViewModels;
using Windows.System;

namespace test.Views;

// TODO: Update NavigationViewItem titles and icons in ShellPage.xaml.
public sealed partial class ShellPage : Page
{
    public ShellViewModel ViewModel { get; }
    private CancellationTokenSource? suggestionCancellationTokenSource;
    private readonly ILocaleService _localeService;

    public ShellPage(ShellViewModel viewModel)
    {
        ViewModel = viewModel;
        _localeService = App.GetService<ILocaleService>();
        InitializeComponent();

        ViewModel.NavigationService.Frame = NavigationFrame;
        ViewModel.NavigationViewService.Initialize(NavigationViewControl);

        // TODO: Set the title bar icon by updating /Assets/WindowIcon.ico.
        // A custom title bar is required for full window theme and Mica support.
        // https://docs.microsoft.com/windows/apps/develop/title-bar?tabs=winui3#full-customization
        App.MainWindow.ExtendsContentIntoTitleBar = true;
        App.MainWindow.AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        App.MainWindow.SetTitleBar(AppTitleBar);
        App.MainWindow.Activated += Window_Activated;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        TitleBarHelper.UpdateTitleBar(RequestedTheme);
        this.AddHandler(PointerPressedEvent, new PointerEventHandler(OnPagePointerPressed), true);
        RegisterBackForwardKeyboardAccelerators();
    }

    private void OnPagePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var properties = e.GetCurrentPoint(this).Properties;
        var result = false;
        // Ignore button chords with the left, right, and middle buttons
        if (
            properties.IsLeftButtonPressed
            || properties.IsRightButtonPressed
            || properties.IsMiddleButtonPressed
        )
            return;

        // If back or forward are pressed (but not both) navigate appropriately
        var backPressed = properties.IsXButton1Pressed;
        var forwardPressed = properties.IsXButton2Pressed;
        if (backPressed ^ forwardPressed)
        {
            if (backPressed)
            {
                result = TryGoBack();
            }
            if (forwardPressed)
            {
                result = TryGoForward();
            }
        }
        e.Handled = result;
    }

    private void RegisterBackForwardKeyboardAccelerators()
    {
        // Back navigation accelerators
        KeyboardAccelerators.Add(
            BuildKeyboardAccelerator(VirtualKey.Left, VirtualKeyModifiers.Menu)
        );
        KeyboardAccelerators.Add(BuildKeyboardAccelerator(VirtualKey.GoBack));

        // Forward navigation accelerators
        KeyboardAccelerators.Add(
            BuildKeyboardAccelerator(VirtualKey.Right, VirtualKeyModifiers.Menu)
        );
        KeyboardAccelerators.Add(BuildKeyboardAccelerator(VirtualKey.GoForward));
    }

    private static KeyboardAccelerator BuildKeyboardAccelerator(
        VirtualKey key,
        VirtualKeyModifiers? modifiers = null
    )
    {
        var keyboardAccelerator = new KeyboardAccelerator() { Key = key };

        if (modifiers.HasValue)
        {
            keyboardAccelerator.Modifiers = modifiers.Value;
        }

        keyboardAccelerator.Invoked += OnKeyboardAcceleratorInvoked;

        return keyboardAccelerator;
    }

    private static void OnKeyboardAcceleratorInvoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args
    )
    {
        var result = false;

        // Check which key was pressed and navigate accordingly
        if (
            sender.Key == VirtualKey.GoBack
            || (sender.Key == VirtualKey.Left && sender.Modifiers == VirtualKeyModifiers.Menu)
        )
        {
            // Back navigation
            result = TryGoBack();
        }
        else if (
            sender.Key == VirtualKey.GoForward
            || (sender.Key == VirtualKey.Right && sender.Modifiers == VirtualKeyModifiers.Menu)
        )
        {
            // Forward navigation
            result = TryGoForward();
        }

        args.Handled = result;
    }

    private static bool TryGoBack()
    {
        var navigationService = App.GetService<INavigationService>();
        if (navigationService.Frame?.CanGoBack == true)
        {
            navigationService.Frame.GoBack();
            return true;
        }

        return false;
    }

    private static bool TryGoForward()
    {
        var navigationService = App.GetService<INavigationService>();
        if (navigationService.Frame?.CanGoForward == true)
        {
            navigationService.Frame.GoForward();
            return true;
        }

        return false;
    }

    private void Window_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            VisualStateManager.GoToState(this, "Deactivated", true);
        }
        else
        {
            VisualStateManager.GoToState(this, "Activated", true);
        }
    }

    private void OnPaneDisplayModeChanged(
        NavigationView sender,
        NavigationViewDisplayModeChangedEventArgs args
    )
    {
        if (args.DisplayMode == NavigationViewDisplayMode.Minimal)
        {
            VisualStateManager.GoToState(this, "Compact", true);
        }
        else
        {
            VisualStateManager.GoToState(this, "Default", true);
        }
    }

    private void SearchBox_QuerySubmitted(
        AutoSuggestBox sender,
        AutoSuggestBoxQuerySubmittedEventArgs args
    )
    {
        // Cancel any pending suggestions
        suggestionCancellationTokenSource?.Cancel();

        // If a suggestion was explicitly chosen:
        if (args.ChosenSuggestion != null)
        {
            if (args.ChosenSuggestion is string suggestionText)
            {
                // Navigate to SearchPage with the string suggestion text.
                NavigationFrame.Navigate(typeof(SearchPage), suggestionText);
            }
            else if (args.ChosenSuggestion is Card cardSuggestion)
            {
                // Navigate to AppPage with the card object.
                NavigationFrame.Navigate(typeof(SearchPage), cardSuggestion);
            }
        }
        else if (!string.IsNullOrWhiteSpace(args.QueryText))
        {
            // No suggestion was chosen; use the typed query for searching.
            NavigationFrame.Navigate(typeof(SearchPage), args.QueryText);
        }

        // Clear the items and text of the AutoSuggestBox.
        sender.ItemsSource = null;
        sender.Text = string.Empty;

        // Close the suggestion list.
        sender.IsSuggestionListOpen = false;
    }

    private void CtrlF_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        SearchBox.Focus(FocusState.Programmatic);
    }

    private async void SearchBox_TextChanged(
        AutoSuggestBox sender,
        AutoSuggestBoxTextChangedEventArgs args
    )
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            var query = sender.Text;
            if (string.IsNullOrWhiteSpace(query))
            {
                sender.ItemsSource = null;
            }
            else
            {
                // Cancel any previous request
                suggestionCancellationTokenSource?.Cancel();
                suggestionCancellationTokenSource = new CancellationTokenSource();

                // Call your API that returns card suggestions.
                var suggestions = await GetCardSuggestionsAsync(
                    query,
                    suggestionCancellationTokenSource.Token
                );

                // Only update UI if not cancelled
                if (!suggestionCancellationTokenSource.IsCancellationRequested)
                {
                    sender.ItemsSource = suggestions;
                }
            }
        }
    }

    private async Task<List<object>> GetCardSuggestionsAsync(
        string query,
        CancellationToken cancellationToken
    )
    {
        DeviceFamily deviceFamily = DeviceFamily.Desktop;
        Market market = _localeService.Market;
        Lang language = _localeService.Language;
        var combined = new List<object>();

        try
        {
            Result<StoreEdgeFDSuggestions> result =
                await StoreEdgeFDSuggestions.GetSearchSuggestion(
                    query,
                    deviceFamily,
                    market,
                    language,
                    cancellationToken
                );

            if (result.IsSuccess)
            {
                combined.AddRange(result.Value.Suggestions);
                combined.AddRange(result.Value.Cards);
            }
            else
            {
                throw result.Exception;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.Message);
        }
        return combined;
    }

    private void InstallationsButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.NavigationService.NavigateTo(typeof(InstallationsViewModel).FullName!);
    }
}

public partial class SuggestionTemplateSelector : DataTemplateSelector
{
    public DataTemplate? StringTemplate { get; set; }
    public DataTemplate? CardTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container)
    {
        if (item is string)
        {
            return StringTemplate;
        }
        else if (item is Card)
        {
            return CardTemplate;
        }
        return base.SelectTemplateCore(item, container);
    }
}
