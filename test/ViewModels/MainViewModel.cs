using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using StoreListings.Library;
using test.Contracts.Services;
using test.Contracts.ViewModels;

namespace test.ViewModels;

public partial class MainViewModel : ObservableRecipient, INavigationAware, ICardViewModel
{
    public int F1Index = 0;
    public int F2Index = 0;
    public MediaTypeRecommendation MediaType = MediaTypeRecommendation.Apps;
    public Category Category = Category.TopFree;

    [ObservableProperty]
    private string headerText = "";

    public ObservableCollection<Card> Cards { get; set; } = [];

    public int CurrentSkipItem { get; set; }

    public double ScrollPosition { get; set; }

    public bool HasMoreItems { get; set; }

    public bool HasCachedResults { get; set; }

    public object Filter1
    {
        get => MediaType;
        set
        {
            if (value is int index)
            {
                MediaType = MediaTypePairs[index];
                F1Index = index;
            }
        }
    }

    public object Filter2
    {
        get => Category;
        set
        {
            if (value is int index)
            {
                Category = CategoryTypePairs[index];
                HeaderText = ItemSourceFilter2[index];
                F2Index = index;
            }
        }
    }

    public readonly List<string> ItemSourceFilter1 = ["Apps", "Games"];

    public readonly List<string> ItemSourceFilter2 =
    [
        "Top Free",
        "Top Paid",
        "Best Rated",
        "New & Trending",
        "Best Selling",
        "Most Popular",
    ];

    private static readonly Dictionary<int, MediaTypeRecommendation> MediaTypePairs = new()
    {
        { 0, MediaTypeRecommendation.Apps },
        { 1, MediaTypeRecommendation.Games },
    };

    private static readonly Dictionary<int, Category> CategoryTypePairs = new()
    {
        { 0, Category.TopFree },
        { 1, Category.TopPaid },
        { 2, Category.BestRated },
        { 3, Category.NewAndRising },
        { 4, Category.TopGrossing },
        { 5, Category.Mostpopular },
    };

    public MainViewModel() { }

    public void OnNavigatedTo(object parameter)
    {
        var frame = App.GetService<INavigationService>().Frame;
        frame?.BackStack.Clear();
    }

    public void OnNavigatedFrom() { }
}
