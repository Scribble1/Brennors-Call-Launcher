using System.Windows;
using Microsoft.Win32;

namespace BrennorsCall.Launcher.Desktop;

public partial class MainWindow : Window
{
    public MainWindow() { InitializeComponent(); }
    private async void SignIn(object sender, RoutedEventArgs e) => await Authenticate(false);
    private async void Register(object sender, RoutedEventArgs e) => await Authenticate(true);
    private async Task Authenticate(bool register)
    {
        var password = Password.Password; Password.Clear();
        await ((LauncherViewModel)DataContext).Login(password, register);
    }
    private void Browse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose the game installation directory" };
        if (dialog.ShowDialog(this) == true) ((LauncherViewModel)DataContext).SetDirectory(dialog.FolderName);
    }
}
