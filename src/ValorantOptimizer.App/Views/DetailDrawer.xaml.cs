using System.Windows.Controls;
using System.Windows.Input;
using ValorantOptimizer.App.ViewModels;

namespace ValorantOptimizer.App.Views;

public partial class DetailDrawer : UserControl
{
    public DetailDrawer()
    {
        InitializeComponent();
    }

    private void Scrim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.CloseDetailsCommand.Execute(null);
        }
    }
}
