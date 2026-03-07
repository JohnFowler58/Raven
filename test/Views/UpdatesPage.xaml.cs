using Microsoft.UI.Xaml.Controls;

using test.ViewModels;

namespace test.Views;

public sealed partial class UpdatesPage : Page
{
    public UpdatesViewModel ViewModel
    {
        get;
    }

    public UpdatesPage()
    {
        ViewModel = App.GetService<UpdatesViewModel>();
        InitializeComponent();
    }
}
