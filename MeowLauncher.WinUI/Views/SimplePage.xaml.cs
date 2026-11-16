using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace MeowLauncher.WinUI.Views;

public sealed record PageSpec(string Title, string Description, string Glyph);

public sealed partial class SimplePage : Page
{
    public SimplePage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not PageSpec spec) return;
        PageTitle.Text = spec.Title;
        PageDescription.Text = spec.Description;
        PageGlyph.Glyph = spec.Glyph;
    }

    private void Home_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) =>
        App.MainWindow.Navigate("home");
}
