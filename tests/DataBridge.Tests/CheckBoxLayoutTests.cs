using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

namespace DataBridge.Tests;

/// <summary>
/// 1.3.2: checking / unchecking a box must never move anything. The real check box style of the application
/// (Themes/Colors.xaml + Themes/Theme.xaml) is loaded and measured in every state, focus included.
/// </summary>
public class CheckBoxLayoutTests
{
    private static readonly string[] GeometryProperties =
        ["Margin", "Padding", "Width", "Height", "MinWidth", "MinHeight", "MaxWidth", "MaxHeight", "BorderThickness", "RenderTransform", "LayoutTransform"];

    [Fact]
    public void State_triggers_only_change_brushes_visibility_and_opacity()
    {
        RunSta(() =>
        {
            var template = CheckBoxStyle().Setters.OfType<Setter>().Single(s => s.Property == Control.TemplateProperty).Value as ControlTemplate;
            Assert.NotNull(template);
            var setters = template!.Triggers.SelectMany(t => t switch
            {
                Trigger tr => tr.Setters.OfType<Setter>(),
                MultiTrigger mt => mt.Setters.OfType<Setter>(),
                _ => [],
            }).ToList();
            Assert.NotEmpty(setters);
            Assert.All(setters, s => Assert.DoesNotContain(s.Property.Name, GeometryProperties));
            Assert.All(setters, s => Assert.Contains(s.Property.Name, new[] { "Background", "BorderBrush", "Visibility", "Opacity" }));
            // The tick and the dash are hidden (not collapsed): they never take part in the layout of the square.
            Assert.DoesNotContain(setters, s => s.Property.Name == "Visibility" && Equals(s.Value, Visibility.Collapsed));
        });
    }

    [Fact]
    public void Focus_ring_is_an_adorner_and_never_part_of_the_layout()
    {
        RunSta(() =>
        {
            var focus = CheckBoxStyle().Setters.OfType<Setter>().Single(s => s.Property == FrameworkElement.FocusVisualStyleProperty).Value;
            Assert.IsType<Style>(focus);
        });
    }

    [Fact]
    public void Same_geometry_in_every_state_with_and_without_focus()
    {
        RunSta(() =>
        {
            var window = new Window
            {
                Width = 400, Height = 200, Left = -20000, Top = -20000, WindowStyle = WindowStyle.None,
                ShowInTaskbar = false, ShowActivated = false,
            };
            window.Resources = Theme();
            var panel = new StackPanel();
            var first = new CheckBox { Content = new TextBlock { Text = "default-release" }, Height = 32 };
            var second = new CheckBox { Content = new TextBlock { Text = "default" }, Height = 32 };
            var alone = new CheckBox(); // no content (Signatures Outlook, Wi-Fi rows)
            panel.Children.Add(first);
            panel.Children.Add(second);
            panel.Children.Add(alone);
            window.Content = panel;
            window.Show();
            try
            {
                string Geometry()
                {
                    window.UpdateLayout();
                    return string.Join(" | ", new[] { first, second, alone }.Select(cb =>
                    {
                        var content = cb.Content as FrameworkElement;
                        var at = content?.TranslatePoint(new Point(0, 0), window);
                        return $"{cb.TranslatePoint(new Point(0, 0), window)} {cb.ActualWidth}x{cb.ActualHeight} {at}";
                    }));
                }

                var reference = Geometry();
                foreach (var state in new bool?[] { true, null, false, true })
                {
                    first.IsChecked = state;
                    alone.IsChecked = state;
                    Assert.Equal(reference, Geometry());
                }
                Assert.True(first.Focus());
                Assert.True(first.IsKeyboardFocused);
                Assert.Equal(reference, Geometry());
                first.IsChecked = false;
                Assert.Equal(reference, Geometry());
                Assert.True(second.Focus());
                second.IsChecked = true;
                Assert.Equal(reference, Geometry());
                Assert.True(alone.Focus());
                alone.IsChecked = !alone.IsChecked;
                Assert.Equal(reference, Geometry());
                first.IsEnabled = false;
                Assert.Equal(reference, Geometry());
            }
            finally
            {
                window.Close();
            }
        });
    }

    // ------------------------------------------------------------------ helpers

    private static Style CheckBoxStyle() => (Style)Theme()[typeof(CheckBox)];

    /// <summary>Colors.xaml + Theme.xaml of the application, merged into one dictionary (as in App.xaml).</summary>
    private static ResourceDictionary Theme()
    {
        static string Body(string file)
        {
            var xaml = File.ReadAllText(Path.Combine(ThemesFolder(), file));
            var start = xaml.IndexOf('>', xaml.IndexOf("<ResourceDictionary", StringComparison.Ordinal)) + 1;
            return xaml[start..xaml.LastIndexOf("</ResourceDictionary>", StringComparison.Ordinal)];
        }
        var merged = "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
                     "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">" + Body("Colors.xaml") + Body("Theme.xaml") + "</ResourceDictionary>";
        return (ResourceDictionary)XamlReader.Parse(merged);
    }

    private static string ThemesFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "DataBridge.App", "Themes");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("src/DataBridge.App/Themes");
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception e) { error = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
}
