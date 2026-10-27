using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BambuBar.Core.I18n;
using Palette = BambuBar.App.Palette;

namespace BambuBar.App;

/// <summary>About the app: version details for bug reports, how it works, privacy and credits.</summary>
public sealed class AboutPage
{
    public FrameworkElement Build()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        string ver = version is null ? "dev" : version.ToString(3);
        string build = version is null ? "dev" : version.Revision.ToString();
        string system = $"{Environment.OSVersion.VersionString} · {Loc.Tf("about.bits", Environment.Is64BitOperatingSystem ? 64 : 32)}";
        string summary = $"Bambu Bar {ver} (build {build}) · {system} · .NET 8";

        var root = new StackPanel
        {
            Margin = new Thickness(28, 24, 28, 24),
            MaxWidth = 860,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        // MARK: header
        var header = new Grid { Margin = new Thickness(0, 0, 0, 22) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var art = new Border
        {
            Width = 96,
            Height = 96,
            CornerRadius = new CornerRadius(22),
            Background = new SolidColorBrush(ThemeEngine.Current.Neutral(0x0A)),
            BorderBrush = new SolidColorBrush(ThemeEngine.Current.Neutral(0x1A)),
            BorderThickness = new Thickness(0.75),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 24, 0),
        };
        var image = TrayIcon.AppImage();
        if (image is not null)
        {
            art.Child = new Image
            {
                Source = image,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(8),
            };
        }
        else
        {
            art.Child = new TextBlock
            {
                Text = "🖨",
                FontSize = 48,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }
        Grid.SetColumn(art, 0);
        header.Children.Add(art);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = "Bambu Bar", FontSize = 30, FontWeight = FontWeights.Bold, Foreground = ThemeEngine.Current.PrimaryBrush });
        text.Children.Add(new TextBlock
        {
            Text = Loc.Tf("about.version", ver, build),
            FontSize = 13,
            Foreground = Ui.Brush(Palette.Idle),
            Margin = new Thickness(0, 3, 0, 0),
        });
        text.Children.Add(new TextBlock
        {
            Text = Loc.T("about.tagline"),
            FontSize = 13,
            Foreground = new SolidColorBrush(ThemeEngine.Current.Neutral(0x66)),
            Margin = new Thickness(0, 1, 0, 0),
        });
        Grid.SetColumn(text, 1);
        header.Children.Add(text);
        root.Children.Add(header);

        // MARK: this copy
        var copy = new StackPanel();
        copy.Children.Add(Ui.DetailRow(Loc.T("about.row_version"), $"{ver} (build {build})"));
        copy.Children.Add(Ui.DetailRow(Loc.T("about.row_system"), system));
        copy.Children.Add(Ui.DetailRow(Loc.T("about.row_license"), Loc.T("about.license_mit")));
        var reportRow = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        reportRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        reportRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        string copyLabel = Loc.T("about.copy_info"), copiedLabel = Loc.T("about.copied");
        var note = new TextBlock { Text = Loc.T("about.bug_reports"), FontSize = 12, Foreground = Ui.Brush(Palette.Idle) };
        Button copyButton = null!;
        copyButton = PageKit.Secondary(copyLabel, () =>
        {
            try
            {
                Clipboard.SetText(summary);
                copyButton.Content = copiedLabel;
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                timer.Tick += (_, _) => { copyButton.Content = copyLabel; timer.Stop(); };
                timer.Start();
            }
            catch
            {
                copyButton.Content = Loc.T("about.clipboard_unavailable");
            }
        });
        Grid.SetColumn(note, 0);
        Grid.SetColumn(copyButton, 1);
        reportRow.Children.Add(note);
        reportRow.Children.Add(copyButton);
        copy.Children.Add(reportRow);
        root.Children.Add(Section(Loc.T("about.section_copy"), copy));

        // MARK: how it works
        var how = new StackPanel();
        how.Children.Add(Paragraph(Loc.T("about.how1")));
        how.Children.Add(Paragraph(Loc.T("about.how2")));
        root.Children.Add(Section(Loc.T("about.section_how"), how));

        // MARK: privacy
        var privacy = new StackPanel();
        privacy.Children.Add(Paragraph(Loc.T("about.privacy")));
        root.Children.Add(Section(Loc.T("about.section_privacy"), privacy));

        // MARK: credits
        var credits = new StackPanel();
        credits.Children.Add(Paragraph(Loc.T("about.credits1")));
        credits.Children.Add(Paragraph(Loc.T("about.credits2")));
        root.Children.Add(Section(Loc.T("about.section_credits"), credits));

        return root;
    }

    private static UIElement Section(string title, UIElement content)
    {
        var block = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        block.Children.Add(Ui.SectionLabel(title));
        block.Children.Add(Ui.Panel(content, radius: 16, padding: 16));
        return block;
    }

    private static TextBlock Paragraph(string text) => new()
    {
        Text = text,
        FontSize = 12.5,
        Foreground = Ui.Brush(Palette.Idle),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 5, 0, 5),
    };
}
