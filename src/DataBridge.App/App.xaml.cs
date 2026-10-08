using System.Windows;
using System.Windows.Threading;
using DataBridge.App.ViewModels;
using DataBridge.Core;

namespace DataBridge.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // 2.1 — elevated printer worker: "DataBridge.exe --printer-worker <pipe> <pid>", started by the restore through
        // ONE UAC prompt when a selected printer needs it. No window; it only executes the closed list of printer
        // operations sent by the DataBridge instance that started it (its PID is checked on the pipe).
        if (e.Args.Length >= 3 && e.Args[0] == Printers.Elevation.WorkerHost.Argument && int.TryParse(e.Args[2], out var parentPid))
        {
            int code;
            try { code = Printers.Elevation.WorkerHost.Run(e.Args[1], parentPid); }
            catch { code = 1; }
            Shutdown(code);
            return;
        }

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
