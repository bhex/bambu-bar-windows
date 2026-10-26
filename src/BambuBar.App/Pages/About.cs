using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
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
        string system = $"{Environment.OSVersion.VersionString} · {(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")}";
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
            Text = $"Version {ver} (build {build})",
            FontSize = 13,
            Foreground = Ui.Brush(Palette.Idle),
            Margin = new Thickness(0, 3, 0, 0),
        });
        text.Children.Add(new TextBlock
        {
            Text = "Your Bambu Lab printer, one click away in the system tray.",
            FontSize = 13,
            Foreground = new SolidColorBrush(ThemeEngine.Current.Neutral(0x66)),
            Margin = new Thickness(0, 1, 0, 0),
        });
        Grid.SetColumn(text, 1);
        header.Children.Add(text);
        root.Children.Add(header);

        // MARK: this copy
        var copy = new StackPanel();
        copy.Children.Add(Ui.DetailRow("Version", $"{ver} (build {build})"));
        copy.Children.Add(Ui.DetailRow("System", system));
        copy.Children.Add(Ui.DetailRow("License", "MIT, free and open source"));
        var reportRow = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        reportRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        reportRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var note = new TextBlock { Text = "For bug reports", FontSize = 12, Foreground = Ui.Brush(Palette.Idle) };
        Button copyButton = null!;
        copyButton = PageKit.Secondary("Copy Version Info", () =>
        {
            try
            {
                Clipboard.SetText(summary);
                copyButton.Content = "Copied ✓";
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                timer.Tick += (_, _) => { copyButton.Content = "Copy Version Info"; timer.Stop(); };
                timer.Start();
            }
            catch
            {
                copyButton.Content = "Clipboard unavailable";
            }
        });
        Grid.SetColumn(note, 0);
        Grid.SetColumn(copyButton, 1);
        reportRow.Children.Add(note);
        reportRow.Children.Add(copyButton);
        copy.Children.Add(reportRow);
        root.Children.Add(Section("This copy", copy));

        // MARK: how it works
        var how = new StackPanel();
        how.Children.Add(Paragraph(
            "Bambu Bar connects straight to your printer on the local network, the same way Bambu Studio does in LAN mode: an encrypted MQTT connection on port 8883, signed in with the printer's access code. The printer's certificate is checked against the Bambu Lab certificate authorities bundled with the app — a connection from anything else is refused."));
        how.Children.Add(Paragraph(
            "The printer streams its status about once a second. Bambu Bar shows it in the system tray, watches the job it starts, and keeps a short history of your prints."));
        root.Children.Add(Section("How it works", how));

        // MARK: privacy
        var privacy = new StackPanel();
        privacy.Children.Add(Paragraph(
            "No account, no cloud, no analytics. Nothing leaves your network: Bambu Bar talks only to your printer. The access code is stored in the Windows Credential Manager — never in plain text — and only ever sent to the printer."));
        root.Children.Add(Section("Privacy", privacy));

        // MARK: credits
        var credits = new StackPanel();
        credits.Children.Add(Paragraph(
            "A Windows port of the independent macOS app Bambu Bar by rolottr (MIT). Protocol details and the English HMS error texts come from the open-source ha-bambulab project (MIT), with thanks to Bambuddy and bambu-cli for documenting the newer printers."));
        credits.Children.Add(Paragraph(
            "Bambu Lab, Bambu Studio and P2S are trademarks of Bambu Lab. Bambu Bar is an independent project, not affiliated with or endorsed by Bambu Lab."));
        root.Children.Add(Section("Credits", credits));

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
