using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DataBridge.App.ViewModels;

namespace DataBridge.App.Views;

public partial class PersonalView : UserControl
{
    public PersonalView()
    {
        InitializeComponent();
    }

    private PersonalViewModel? Vm => DataContext as PersonalViewModel;

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not ListBox list || Vm is null) return;
        switch (e.Key)
        {
            case Key.Space:
                Vm.ToggleItems(list.SelectedItems);
                e.Handled = true;
                break;
            case Key.Right when list.SelectedItem is TreeRowViewModel { IsExpanded: false, HasChildren: true } row:
                row.IsExpanded = true;
                e.Handled = true;
                break;
            case Key.Left when list.SelectedItem is TreeRowViewModel { IsExpanded: true } row:
                row.IsExpanded = false;
                e.Handled = true;
                break;
        }
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Double-click opens/closes a folder, unless the click was on the checkbox or the chevron.
        for (var d = e.OriginalSource as DependencyObject; d != null && d is not ListBoxItem; d = VisualTreeHelper.GetParent(d))
            if (d is ToggleButton) return;
        if (sender is ListBox { SelectedItem: TreeRowViewModel { HasChildren: true } row })
        {
            row.IsExpanded = !row.IsExpanded;
            e.Handled = true;
        }
    }
}

/// <summary>Root rows (personal folders) and entry rows (folders/files) use different templates.</summary>
public sealed class RowTemplateSelector : DataTemplateSelector
{
    public DataTemplate? RootTemplate { get; set; }
    public DataTemplate? EntryTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        item is TreeRowViewModel { IsRoot: true } ? RootTemplate : EntryTemplate;
}
