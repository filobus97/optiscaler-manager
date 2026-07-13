// OptiScaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using OptiscalerManager.Core.Components;

namespace OptiscalerManager.App.Views;

public partial class PreviewDialog : Window
{
    // Parameterless ctor for the XAML previewer only.
    public PreviewDialog() { InitializeComponent(); }

    public PreviewDialog(string gameName, InstallPreview preview) : this()
    {
        var title = this.FindControl<TextBlock>("TitleText");
        if (title is not null) title.Text = $"Enable FSR 4 — {gameName}";

        PopulateConflicts(preview);
        PopulateFiles(preview);
        PopulateIni(preview);
        PopulateComponents(preview);
    }

    private void PopulateConflicts(InstallPreview preview)
    {
        if (preview.Conflicts.Count == 0) return;
        var box = this.FindControl<Border>("ConflictBox");
        var list = this.FindControl<StackPanel>("ConflictList");
        if (box is null || list is null) return;

        box.IsVisible = true;
        list.Children.Add(Mono("⚠ Conflicting components requested:", FontWeight.Bold, "#E06060"));
        foreach (var c in preview.Conflicts)
            list.Children.Add(Mono("• " + c, FontWeight.Normal, "#E06060"));

        var confirm = this.FindControl<Button>("ConfirmButton");
        if (confirm is not null) confirm.IsEnabled = false;
    }

    private void PopulateFiles(InstallPreview preview)
    {
        var list = this.FindControl<StackPanel>("FilesList");
        if (list is null) return;
        if (preview.Files.Count == 0)
            list.Children.Add(Mono("(no files)", FontWeight.Normal, "#8A8AAA"));
        foreach (var f in preview.Files)
            list.Children.Add(Mono(f));
    }

    private void PopulateIni(InstallPreview preview)
    {
        var list = this.FindControl<StackPanel>("IniList");
        if (list is null) return;
        if (preview.IniKeys.Count == 0)
            list.Children.Add(Mono("(no ini changes)", FontWeight.Normal, "#8A8AAA"));
        foreach (var k in preview.IniKeys)
            list.Children.Add(Mono(k.ToString()));
    }

    private void PopulateComponents(InstallPreview preview)
    {
        var list = this.FindControl<StackPanel>("ComponentsList");
        if (list is null) return;
        foreach (var c in preview.Components)
        {
            var panel = new StackPanel { Spacing = 2 };
            panel.Children.Add(new TextBlock
            {
                Text = c.Component.DisplayName + (c.Component.IsBringYourOwn ? "  (your imported file)" : ""),
                FontWeight = FontWeight.SemiBold,
                Foreground = Brush("#E4E4EF"),
            });
            panel.Children.Add(new TextBlock
            {
                Text = c.Component.Description,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("#8A8AAA"),
            });
            list.Children.Add(panel);
        }
    }

    private static TextBlock Mono(string text, FontWeight weight = FontWeight.Normal, string color = "#E4E4EF")
        => new()
        {
            Text = text,
            FontFamily = new FontFamily("Cascadia Code, Consolas, monospace"),
            FontSize = 12,
            FontWeight = weight,
            Foreground = Brush(color),
            TextWrapping = TextWrapping.Wrap,
        };

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));

    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    /// <summary>Shows the dialog modally and returns whether the user confirmed.</summary>
    public Task<bool> ShowDialogFor(Window owner) => ShowDialog<bool>(owner);
}
