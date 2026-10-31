using System.Windows;
namespace CompactLoader;
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        await ThemeManager.Load();
        base.OnStartup(e);
        MainWindow = new MainWindow();
        MainWindow.Show();
    }
}
