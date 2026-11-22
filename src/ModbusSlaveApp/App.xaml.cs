// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.Windows;
using ModbusTool.App;

namespace ModbusSlaveApp;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        AppHost.OnStartup(this, "ModbusSlave", e);
        base.OnStartup(e);
        SlaveWindow window = new();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppHost.OnExit(e);
        base.OnExit(e);
    }
}
