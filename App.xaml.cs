using System.Windows;
namespace CompactLoader;
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        ThemeManager.Load();
        base.OnStartup(e);
    }
}
