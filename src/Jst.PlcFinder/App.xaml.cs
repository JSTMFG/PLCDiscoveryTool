using System.Windows;
using System.Diagnostics;

namespace Jst.PlcFinder;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            Trace.WriteLine(args.Exception);
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished || MainWindow is MainWindow { IsShutdownInProgress: true })
            {
                args.Handled = true;
                return;
            }

            MessageBox.Show(MainWindow, args.Exception.Message, "PLC Finder", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        bool notificationSmoke = e.Args.Contains("--notification-smoke-test");
        var window = new MainWindow(e.Args.Contains("--smoke-test") || notificationSmoke, notificationSmoke);
        MainWindow = window;
        window.Show();
    }
}
