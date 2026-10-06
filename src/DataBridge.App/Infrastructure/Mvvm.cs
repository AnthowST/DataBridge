using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace DataBridge.App.Infrastructure;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute()) { }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => execute(parameter);
}

/// <summary>ObservableCollection able to insert/remove many items with a single Reset notification.</summary>
public sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    private bool _suppress;

    public void InsertRange(int index, IReadOnlyList<T> items)
    {
        if (items.Count < 64)
        {
            for (var i = 0; i < items.Count; i++) Insert(index + i, items[i]);
            return;
        }
        _suppress = true;
        try
        {
            for (var i = 0; i < items.Count; i++) Items.Insert(index + i, items[i]);
        }
        finally
        {
            _suppress = false;
        }
        RaiseReset();
    }

    public void RemoveRange(int index, int count)
    {
        if (count < 64)
        {
            for (var i = 0; i < count; i++) RemoveAt(index);
            return;
        }
        _suppress = true;
        try
        {
            for (var i = 0; i < count; i++) Items.RemoveAt(index);
        }
        finally
        {
            _suppress = false;
        }
        RaiseReset();
    }

    public void ReplaceAll(IEnumerable<T> items)
    {
        _suppress = true;
        try
        {
            Items.Clear();
            foreach (var item in items) Items.Add(item);
        }
        finally
        {
            _suppress = false;
        }
        RaiseReset();
    }

    private void RaiseReset()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (!_suppress) base.OnCollectionChanged(e);
    }
}
