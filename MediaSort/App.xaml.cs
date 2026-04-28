using System.Windows;
using MediaSort.Services;

namespace MediaSort;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        ThemeManager.Initialize();
        base.OnStartup(e);
    }
}
