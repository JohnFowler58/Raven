using System.Collections.ObjectModel;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using StoreListings.Library;
using Raven.Helpers;
using Raven.Models;
using Raven.ViewModels;

namespace Raven.Views;

public sealed partial class BundlesPage : Page
{
    public AppInfo AppData { get; set; } = new AppInfo();

    public ObservableCollection<Card> BundleCards { get; set; } = [];

    private readonly Compositor _compositor;
    private SpringVector3NaturalMotionAnimation? _springAnimation;

    public BundlesViewModel ViewModel { get; }

    public BundlesPage()
    {
        ViewModel = App.GetService<BundlesViewModel>();
        _compositor = App.MainWindow.Compositor;
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is (ProductData productInfo, StoreEdgeFDQuery bundleInfo))
        {
            LoadData(productInfo, bundleInfo);
        }
    }

    private void LoadData(ProductData productInfo, StoreEdgeFDQuery bundleInfo)
    {
        DisplayItem.Visibility = Visibility.Collapsed;
        ErrorIcon.Visibility = Visibility.Collapsed;
        LoadingOverlay.Visibility = Visibility.Visible;

        var success = true;

        AppData.SetValues(
            productInfo.ProductId,
            productInfo.Logo,
            productInfo.Screenshots,
            productInfo.RevisionId,
            productInfo.Version,
            productInfo.Title,
            productInfo.PublisherName,
            productInfo.Description,
            productInfo.Rating,
            productInfo.RatingCount,
            productInfo.Size
        );

        if (bundleInfo.Cards.Count == 0)
        {
            success = false;
        }
        for (var i = 0; i < bundleInfo.Cards.Count; i++)
        {
            var card = bundleInfo.Cards[i];
            BundleCards.Add(card);
        }

        if (success == true)
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
            DisplayItem.Visibility = Visibility.Visible;
        }
        else
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
            ErrorIcon.Visibility = Visibility.Visible;
        }
    }

    private void Card_Tapped(object sender, TappedRoutedEventArgs e)
    {
        Utils.HandleCardTapped(
            sender as FrameworkElement,
            Frame,
            DisplayItem,
            ErrorIcon,
            LoadingOverlay,
            ErrorIconText
        );
    }

    private void element_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        Utils.HandlePointerEntered(sender, e, ref _springAnimation, _compositor);
    }

    private void element_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        Utils.HandlePointerExited(sender, e, ref _springAnimation, _compositor);
    }
}
