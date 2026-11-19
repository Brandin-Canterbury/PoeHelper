using System.Diagnostics;
using System.Windows;

namespace PoeHelper.App;

public partial class SessionHelpWindow : Window
{
    public SessionHelpWindow() => InitializeComponent();

    void OnOpenSite(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://www.pathofexile.com/login") { UseShellExecute = true });

    void OnClose(object sender, RoutedEventArgs e) => Close();
}
