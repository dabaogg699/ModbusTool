// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.Windows;
using ModbusTool.App;

namespace ModbusMasterApp;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        AppHost.OnStartup(this, "ModbusMaster", e);
        base.OnStartup(e);
        MasterWindow window = new(AppHost.ResolveWorkspacePath(e.Args));
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppHost.OnExit(e);
        base.OnExit(e);
    }
}
