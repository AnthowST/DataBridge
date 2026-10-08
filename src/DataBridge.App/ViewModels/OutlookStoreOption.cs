using DataBridge.App.Infrastructure;
using DataBridge.Core.Outlook;

namespace DataBridge.App.ViewModels;

/// <summary>A mailbox of the Outlook profile, shown as a radio button (backup source or restore target).</summary>
public sealed class OutlookStoreOption(StoreEntry entry, Func<StoreEntry?> chosen, Action<StoreEntry> choose) : ObservableObject
{
    public StoreEntry Entry { get; } = entry;
    public string Label => string.IsNullOrWhiteSpace(Entry.DisplayName) ? Entry.Description : Entry.DisplayName;
    public string Detail => Entry.Description;
    public bool IsDefault => Entry.IsDefault;

    public bool IsChosen
    {
        get => chosen()?.StoreId == Entry.StoreId;
        set
        {
            if (value) choose(Entry);
        }
    }

    public void Refresh() => OnPropertyChanged(nameof(IsChosen));
}
