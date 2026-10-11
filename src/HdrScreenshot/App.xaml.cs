using System.Windows;
using System.Windows.Threading;

namespace HdrScreenshot;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandled;
        base.OnStartup(e);
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "HDR Screenshot", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
