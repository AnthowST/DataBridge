using System.Windows;
using System.Windows.Threading;
using DataBridge.App.ViewModels;
using DataBridge.Core;

namespace DataBridge.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;

        // 2.0: DataBridge opens on its home screen (Sauvegarder / Restaurer).
        var vm = new ShellViewModel();
        var window = new MainWindow { DataContext = vm };
        MainWindow = window;
        window.Show();
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Never crash the technician's session on an unexpected UI error: report it and keep going.
        try
        {
            Views.DataBridgeDialog.ShowError(Current?.MainWindow, "Erreur inattendue : " + e.Exception.Message);
        }
        catch
        {
            MessageBox.Show("Erreur inattendue : " + e.Exception.Message, AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        e.Handled = true;
    }
}
