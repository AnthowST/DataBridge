using System.ComponentModel;
using System.Windows;
using DataBridge.App.ViewModels;

namespace DataBridge.App;

public partial class MainWindow : Window
{
    private bool _closingAfterCancel;

    public MainWindow()
    {
        InitializeComponent();
    }

    private ShellViewModel? Shell => DataContext as ShellViewModel;

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_closingAfterCancel || Shell is not { } shell) return;

        // Browsers are being closed just before a copy / restore starts (a few seconds): closing now would start the
        // operation after the window is gone. Wait for the launch to finish (or be cancelled) instead.
        if (shell.Backup is { IsLaunching: true } || shell.Restore is { IsLaunching: true })
        {
            e.Cancel = true;
            return;
        }

        if (shell.Backup is { IsCopying: true } backup)
        {
            e.Cancel = true;
            if (!Views.DataBridgeDialog.ConfirmCloseDuringCopy(this)) return;
            backup.Copy.CancelWithoutPrompt();
            if (backup.Copy.RunningTask is { } task)
            {
                try { await task; } catch { /* result already reported */ }
            }
            _closingAfterCancel = true;
            Close();
            return;
        }

        if (shell.Restore is { IsRestoring: true } restore)
        {
            e.Cancel = true;
            if (!Views.DataBridgeDialog.ConfirmCloseDuringRestore(this)) return;
            restore.Progress.CancelWithoutPrompt();
            if (restore.Progress.RunningTask is { } task)
            {
                try { await task; } catch { /* result already reported */ }
            }
            _closingAfterCancel = true;
            Close();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        Shell?.Dispose();
        base.OnClosed(e);
    }
}
