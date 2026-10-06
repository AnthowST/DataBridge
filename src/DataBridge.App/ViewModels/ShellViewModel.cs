using System.Windows.Input;
using DataBridge.App.Infrastructure;
using DataBridge.Core;
using DataBridge.Core.Profile;

namespace DataBridge.App.ViewModels;

/// <summary>
/// Root of the window. DataBridge opens on the home screen (Sauvegarder / Restaurer); each mode is its own wizard,
/// created when chosen. Changing mode is only possible while no copy or restore is running.
/// </summary>
public sealed class ShellViewModel : ObservableObject, IDisposable
{
    private object _current;
    private MainViewModel? _backup;
    private RestoreViewModel? _restore;

    public ShellViewModel()
    {
        Profile = ProfileInfo.Detect();
        Home = new ModeChoiceViewModel(this);
        _current = Home;
        ChooseBackupCommand = new RelayCommand(StartBackup);
        ChooseRestoreCommand = new RelayCommand(StartRestore);
        HomeCommand = new RelayCommand(GoHome, () => !IsHome && !IsBusy);
    }

    /// <summary>The current Windows session (header).</summary>
    public ProfileInfo Profile { get; }
    public ModeChoiceViewModel Home { get; }
    public ICommand ChooseBackupCommand { get; }
    public ICommand ChooseRestoreCommand { get; }
    /// <summary>Header button "Accueil DataBridge": back to the home screen, never during a copy / restore.</summary>
    public ICommand HomeCommand { get; }

    /// <summary>ModeChoiceViewModel, MainViewModel (backup) or RestoreViewModel.</summary>
    public object Current
    {
        get => _current;
        private set
        {
            if (!Set(ref _current, value)) return;
            OnPropertyChanged(nameof(WindowTitle));
            OnPropertyChanged(nameof(IsHome));
        }
    }

    public bool IsHome => Current is ModeChoiceViewModel;
    public MainViewModel? Backup => _backup;
    public RestoreViewModel? Restore => _restore;

    public string WindowTitle => Current switch
    {
        MainViewModel b => b.WindowTitle,
        RestoreViewModel r => r.WindowTitle,
        _ => $"{AppInfo.Name} — {AppInfo.Baseline}",
    };

    public string VersionText => $"{AppInfo.Name} {AppInfo.Version}";

    /// <summary>True while an operation forbids leaving the current wizard.</summary>
    public bool IsBusy => (_backup is { } b && (b.IsCopying || b.IsLaunching)) || (_restore is { } r && (r.IsRestoring || r.IsLaunching));

    public void StartBackup()
    {
        if (_restore is { IsRestoring: true } || _restore is { IsLaunching: true }) return;
        _restore?.Deactivate();
        if (_backup is { IsFinished: true })
        {
            _backup.Dispose();
            _backup = null;
        }
        if (_backup is null) _backup = new MainViewModel(this);
        else _backup.Activate();
        Current = _backup;
    }

    public void StartRestore()
    {
        if (_backup is { IsCopying: true } || _backup is { IsLaunching: true }) return;
        _backup?.Deactivate();
        if (_restore is { IsFinished: true })
        {
            _restore.Dispose();
            _restore = null;
        }
        if (_restore is null) _restore = new RestoreViewModel(this);
        else _restore.Activate();
        Current = _restore;
    }

    /// <summary>Back to the home screen. A finished wizard is dropped (the next one starts from scratch).</summary>
    public void GoHome()
    {
        if (IsBusy) return;
        if (_backup is { IsFinished: true })
        {
            _backup.Dispose();
            _backup = null;
        }
        else _backup?.Deactivate();
        if (_restore is { IsFinished: true })
        {
            _restore.Dispose();
            _restore = null;
        }
        else _restore?.Deactivate();
        Home.Refresh();
        Current = Home;
    }

    public void NotifyTitle() => OnPropertyChanged(nameof(WindowTitle));

    public void Dispose()
    {
        _backup?.Dispose();
        _restore?.Dispose();
    }
}

/// <summary>Home screen: choose between backing up this profile and restoring a backup into it.</summary>
public sealed class ModeChoiceViewModel(ShellViewModel shell) : ObservableObject
{
    public ShellViewModel Shell { get; } = shell;
    public ProfileInfo Profile => Shell.Profile;
    public ICommand BackupCommand => Shell.ChooseBackupCommand;
    public ICommand RestoreCommand => Shell.ChooseRestoreCommand;
    public string VersionText => Shell.VersionText;

    /// <summary>"Reprendre" hints when a wizard was left with choices in progress.</summary>
    public string BackupHint => Shell.Backup is { IsFinished: false } ? "Sélection en cours conservée" : "";
    public string RestoreHint => Shell.Restore is { IsFinished: false, HasBackup: true } r ? $"Sauvegarde en cours : {r.BackupName}" : "";

    public void Refresh()
    {
        OnPropertyChanged(nameof(BackupHint));
        OnPropertyChanged(nameof(RestoreHint));
    }
}
