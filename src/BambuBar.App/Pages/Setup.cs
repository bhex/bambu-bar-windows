using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BambuBar.Core.App;
using BambuBar.Core.Discovery;
using BambuBar.Core.Models;
using BambuBar.Core.Store;
using Palette = BambuBar.App.Palette;

namespace BambuBar.App;

/// <summary>
/// First run / add printer (port of the macOS <c>SetupPage</c>): pick the printer from the network
/// scan (or type its address), take the access code from a slicer when one knows it, else type it.
/// </summary>
public sealed class SetupPage
{
    private readonly PageContext _ctx;
    /// <summary>
    /// The setup draft, persisted on the window. <c>MainWindow.Refresh</c> recreates this page
    /// whenever the store changes, so any state that must survive a click/keystroke lives there.
    /// </summary>
    private SetupState _s => _ctx.Window.Setup;

    public SetupPage(PageContext ctx) => _ctx = ctx;

    public FrameworkElement Build()
    {
        var store = _ctx.Store;
        bool adding = store.Sessions.Count > 0;

        var root = new StackPanel { Margin = new Thickness(28, 24, 28, 24), MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Center };

        if (!adding)
        {
            root.Children.Add(Hero());
        }
        else
        {
            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(new TextBlock { Text = "Add a Printer", FontSize = 26, FontWeight = FontWeights.Bold, Foreground = ThemeEngine.Current.PrimaryBrush });
            var cancel = PageKit.Secondary("Cancel", () => _ctx.Window.Navigate(MainWindow.SectionPrinter));
            cancel.Margin = new Thickness(0, 6, 0, 0);
            Grid.SetColumn(cancel, 1);
            header.Children.Add(cancel);
            root.Children.Add(header);
            root.Children.Add(new TextBlock
            {
                Text = "Bambu Bar watches every printer you add; pick which one the tray shows in Settings.",
                FontSize = 12,
                Foreground = Ui.Brush(Palette.Idle),
                Margin = new Thickness(0, 4, 0, 16),
            });
        }

        // MARK: step 1 — choose printer
        var step1 = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        var step1Head = new StackPanel { Orientation = Orientation.Horizontal };
        step1Head.Children.Add(new TextBlock { Text = "1  CHOOSE YOUR PRINTER", FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush(Palette.Idle), VerticalAlignment = VerticalAlignment.Center });
        step1Head.Children.Add(new System.Windows.Controls.ProgressBar { Width = 16, Height = 16, IsIndeterminate = store.Discovered.Count == 0, Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        step1Head.Children.Add(PageKit.Secondary("Search Again", () => store.SearchForPrinters()));
        step1.Children.Add(step1Head);

        var newPrinters = store.Discovered.Where(d => store.Session(d.Serial) is null).ToList();
        var slicerOnly = store.SlicerPrinters.Where(p => p.Host is not null && store.Session(p.Serial) is null
            && !store.Discovered.Any(d => d.Serial == p.Serial)).ToList();

        if (newPrinters.Count == 0 && slicerOnly.Count == 0)
        {
            step1.Children.Add(Ui.Panel(new TextBlock
            {
                Text = "Looking for printers on this network. Printers announce themselves every few seconds; make sure yours is on and on the same network as this PC.",
                FontSize = 12,
                Foreground = Ui.Brush(Palette.Idle),
                TextWrapping = TextWrapping.Wrap,
            }, radius: 14, padding: 12));
        }

        foreach (var printer in newPrinters)
            step1.Children.Add(DiscoveredRow(printer, fromSlicer: false));

        foreach (var printer in slicerOnly)
        {
            var fake = new DiscoveredPrinter { Serial = printer.Serial, Host = printer.Host ?? "", Name = printer.Name ?? printer.Serial, ModelCode = "" };
            step1.Children.Add(DiscoveredRow(fake, fromSlicer: true, source: printer.Source));
        }

        step1.Children.Add(ManualEntry());
        root.Children.Add(step1);

        // MARK: step 2 — access code
        var step2 = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
        step2.Children.Add(Ui.SectionLabel("2  Access code"));
        step2.Children.Add(CodeSection());
        root.Children.Add(step2);

        // MARK: open at login
        if (!adding)
        {
            var toggle = new CheckBox
            {
                Content = "Open Bambu Bar at login (it starts quietly in the tray)",
                IsChecked = _s.OpenAtLogin,
                FontSize = 12.5,
                Margin = new Thickness(0, 18, 0, 0),
            };
            toggle.Checked += (_, _) => _s.OpenAtLogin = true;
            toggle.Unchecked += (_, _) => _s.OpenAtLogin = false;
            root.Children.Add(toggle);
        }

        // MARK: connect
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 20, 0, 0) };
        var connect = PageKit.Primary("Connect", Connect, width: 150);
        footer.Children.Add(connect);
        _connectButton = connect;
        UpdateConnect(); // reflect a draft restored from a previous visit
        root.Children.Add(footer);

        return root;
    }

    private Button _connectButton = null!;

    private void Connect()
    {
        var store = _ctx.Store;
        var s = _s;
        if (s.Host.Trim().Length == 0 || s.Serial.Trim().Length == 0 || s.Code.Trim().Length < 4) return;

        var config = new PrinterConfig
        {
            Host = s.Host.Trim(),
            Serial = s.Serial.Trim().ToUpperInvariant(),
            Name = s.Name.Trim().Length > 0 ? s.Name.Trim() : s.Serial.Trim(),
            ModelCode = s.ModelCode,
        };
        bool first = store.Sessions.Count == 0;
        store.Connect(config, s.Code.Trim(), s.AlternativeCodes.Count > 0 ? s.AlternativeCodes : null);
        if (first && s.OpenAtLogin && !AutoStart.IsEnabled)
            AutoStart.SetEnabled(true);
        _ctx.Window.Navigate(MainWindow.SectionPrinter);
    }

    private UIElement Hero()
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 18) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var art = new Border
        {
            Width = 110,
            Height = 110,
            CornerRadius = new CornerRadius(24),
            Background = Ui.Brush(Palette.AccentDeep),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 24, 0),
            Child = new TextBlock
            {
                Text = "🖨",
                FontSize = 52,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        Grid.SetColumn(art, 0);
        grid.Children.Add(art);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = "Welcome to Bambu Bar", FontSize = 28, FontWeight = FontWeights.Bold, Foreground = ThemeEngine.Current.PrimaryBrush });
        text.Children.Add(new TextBlock
        {
            Text = "Your printer's progress, temperatures and filament, one click away in the system tray. Bambu Bar talks to the printer directly over your network: no account, no cloud.",
            FontSize = 13.5,
            Foreground = Ui.Brush(Palette.Idle),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    private UIElement DiscoveredRow(DiscoveredPrinter printer, bool fromSlicer, string source = "")
    {
        bool selected = _s.Selected == printer.Serial;
        var slicer = _ctx.Store.SlicerPrinters.FirstOrDefault(p => p.Serial == printer.Serial);

        var content = new StackPanel();
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var radio = new TextBlock
        {
            Text = selected ? "◉" : "○",
            FontSize = 16,
            Foreground = selected ? Ui.Brush(Palette.Accent) : Ui.Brush(Palette.Idle),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        };
        Grid.SetColumn(radio, 0);
        row.Children.Add(radio);

        var name = new StackPanel();
        name.Children.Add(new TextBlock { Text = printer.Name.Length > 0 ? printer.Name : printer.Serial, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = ThemeEngine.Current.PrimaryBrush });
        name.Children.Add(new TextBlock
        {
            Text = $"Bambu Lab {printer.ModelName} · {printer.Host} · {printer.Serial}" + (printer.ConnectMode is { Length: > 0 } m ? (m.Equals("lan", StringComparison.OrdinalIgnoreCase) ? " · LAN Only" : " · Cloud") : ""),
            FontSize = 11.5,
            Foreground = Ui.Brush(Palette.Idle),
        });
        Grid.SetColumn(name, 1);
        row.Children.Add(name);

        if (slicer is not null)
        {
            var badge = new Border
            {
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(8, 2, 8, 2),
                Background = Ui.Brush(Color.FromArgb(0x1F, Palette.Accent.R, Palette.Accent.G, Palette.Accent.B)),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = $"Code in {slicer.Source}", FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush(Palette.AccentDeep) },
            };
            Grid.SetColumn(badge, 2);
            row.Children.Add(badge);
        }

        content.Children.Add(row);

        var button = new Button
        {
            Content = content,
            Background = selected ? new SolidColorBrush(Color.FromArgb(0x1F, Palette.Accent.R, Palette.Accent.G, Palette.Accent.B)) : Brushes.Transparent,
            BorderBrush = selected ? new SolidColorBrush(Color.FromArgb(0x66, Palette.Accent.R, Palette.Accent.G, Palette.Accent.B)) : new SolidColorBrush(ThemeEngine.Current.Neutral(0x1A)),
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        var border = new Border
        {
            Child = button,
            CornerRadius = new CornerRadius(14),
            Margin = new Thickness(0, 4, 0, 4),
        };
        button.Click += (_, _) => Pick(printer, slicer);
        return border;
    }

    private void Pick(DiscoveredPrinter printer, SlicerPrinter? slicer)
    {
        var s = _s;
        s.Selected = printer.Serial;
        s.Host = printer.Host;
        s.Serial = printer.Serial;
        s.Name = printer.Name;
        s.ModelCode = printer.ModelCode.Length > 0 ? printer.ModelCode : "N7";

        // Access codes are per-printer: picking a printer always resets the code
        // (a slicer-provided code for exactly this printer is the only exception).
        s.Code = "";
        s.CodeSource = null;
        s.AlternativeCodes = new List<string>();
        if (slicer is not null)
        {
            s.Code = slicer.AccessCodes.Count > 0 ? slicer.AccessCodes[0] : "";
            s.AlternativeCodes = slicer.AccessCodes.Skip(1).ToList();
            s.CodeSource = slicer.Source;
        }
        Rebuild();
    }

    private UIElement ManualEntry()
    {
        var content = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        content.Children.Add(new TextBlock { Text = "Enter address manually", FontSize = 12, FontWeight = FontWeights.Medium, Foreground = ThemeEngine.Current.PrimaryBrush, Margin = new Thickness(0, 0, 0, 8) });

        content.Children.Add(ManualField("IP address", () => _s.Host, v => _s.Host = v, "192.168.1.50"));
        content.Children.Add(ManualField("Serial number", () => _s.Serial, v => _s.Serial = v, "22E…"));
        content.Children.Add(ManualField("Name", () => _s.Name, v => _s.Name = v, "My P2S"));
        return content;
    }

    private UIElement ManualField(string title, Func<string> getter, Action<string> setter, string prompt)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
        var label = new TextBlock { Text = title, FontSize = 12, Foreground = Ui.Brush(Palette.Idle), VerticalAlignment = VerticalAlignment.Center };
        var box = new TextBox
        {
            Text = getter(),
            FontSize = 12.5,
            Padding = new Thickness(7, 5, 7, 5),
            Background = ThemeEngine.Current.InputBackground,
            Foreground = ThemeEngine.Current.PrimaryBrush,
        };
        box.LostFocus += (_, _) => { setter(box.Text); _s.Selected = null; Rebuild(); };
        box.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { setter(box.Text); Rebuild(); } };
        Grid.SetColumn(label, 0);
        Grid.SetColumn(box, 1);
        grid.Children.Add(label);
        grid.Children.Add(box);
        return grid;
    }

    private UIElement CodeSection()
    {
        // A code a slicer already has for the chosen printer.
        if (_s.CodeSource is { Length: > 0 } source)
        {
            var card = new StackPanel();
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var key = new TextBlock { Text = "🔑", FontSize = 15, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            var found = new StackPanel();
            found.Children.Add(new TextBlock { Text = $"Found in {source}", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = ThemeEngine.Current.PrimaryBrush });
            found.Children.Add(new TextBlock
            {
                Text = "Bambu Bar uses the access code saved there for this printer. It's kept in the Windows Credential Manager and only ever sent to the printer.",
                FontSize = 11.5,
                Foreground = Ui.Brush(Palette.Idle),
                TextWrapping = TextWrapping.Wrap,
            });
            var button = PageKit.Secondary("Type a Code Instead", () => { var s = _s; s.CodeSource = null; s.Code = ""; s.AlternativeCodes = new List<string>(); Rebuild(); });
            button.Margin = new Thickness(10, 0, 0, 0);
            Grid.SetColumn(key, 0);
            Grid.SetColumn(found, 1);
            Grid.SetColumn(button, 2);
            row.Children.Add(key);
            row.Children.Add(found);
            row.Children.Add(button);
            card.Children.Add(row);
            return Ui.Panel(card, radius: 16, padding: 14, tint: Palette.Accent);
        }

        var content = new StackPanel();
        var box = new PasswordBox
        {
            FontSize = 15,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            Width = 260,
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(8, 6, 8, 6),
            // Restore a code typed before a rebuild (e.g. after "Search Again").
            Password = _s.Code,
        };
        box.PasswordChanged += (_, _) => { _s.Code = box.Password; UpdateConnect(); };
        box.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) Connect(); };
        content.Children.Add(box);
        content.Children.Add(new TextBlock
        {
            Text = "On the printer's screen open Settings → Network (the Wi-Fi/LAN page) and read the Access Code. It's stored in the Credential Manager and only ever sent to the printer.",
            FontSize = 11.5,
            Foreground = Ui.Brush(Palette.Idle),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 10),
            MaxWidth = 560,
        });

        // Import a slicer settings file.
        var import = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        import.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        import.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        import.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new TextBlock { Text = "⬇", FontSize = 14, Foreground = Ui.Brush(Palette.Idle), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = "Bambu Studio or OrcaSlicer on another PC?", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = ThemeEngine.Current.PrimaryBrush });
        text.Children.Add(new TextBlock
        {
            Text = _s.ImportNote ?? "Choose its settings file — it's in %APPDATA%\\BambuStudio or %APPDATA%\\OrcaSlicer under that user's home folder.",
            FontSize = 11.5,
            Foreground = Ui.Brush(Palette.Idle),
            TextWrapping = TextWrapping.Wrap,
        });
        var choose = PageKit.Secondary("Choose File…", ChooseSettingsFile);
        choose.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(icon, 0);
        Grid.SetColumn(text, 1);
        Grid.SetColumn(choose, 2);
        import.Children.Add(icon);
        import.Children.Add(text);
        import.Children.Add(choose);
        content.Children.Add(import);
        return content;
    }

    private void ChooseSettingsFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose Bambu Studio's or OrcaSlicer's settings",
            Filter = "Slicer settings (*.conf)|*.conf|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true) return;
        ImportSettingsFile(dialog.FileName);
    }

    private void ImportSettingsFile(string path)
    {
        var store = _ctx.Store;
        var found = SlicerImport.Read(path, Path.GetFileName(Path.GetDirectoryName(path)) ?? "slicer");
        if (found.Count == 0)
        {
            _s.ImportNote = $"No access codes found in {Path.GetFileName(path)}.";
            Rebuild();
            return;
        }
        _s.ImportNote = null;
        foreach (var printer in found)
            if (!store.SlicerPrinters.Any(p => p.Serial == printer.Serial))
                store.SlicerPrinters.Add(printer);

        if (_s.Serial.Length == 0 && found.Count > 0)
        {
            var first = found[0];
            var discovered = store.Discovered.FirstOrDefault(d => d.Serial == first.Serial);
            if (discovered is not null) Pick(discovered, first);
            else if (first.Host is { } host)
                Pick(new DiscoveredPrinter { Serial = first.Serial, Host = host, Name = first.Serial, ModelCode = "" }, first);
            else _s.Serial = first.Serial;
        }
        else if (_s.Serial.Length > 0)
        {
            var slicer = store.SlicerPrinters.FirstOrDefault(p => p.Serial == _s.Serial);
            if (slicer is not null && _s.CodeSource is null && _s.Code.Length == 0)
            {
                _s.Code = slicer.AccessCodes.Count > 0 ? slicer.AccessCodes[0] : "";
                _s.AlternativeCodes = slicer.AccessCodes.Skip(1).ToList();
                _s.CodeSource = slicer.Source;
            }
        }
        Rebuild();
    }

    private void UpdateConnect()
    {
        if (_connectButton is not null)
            _connectButton.IsEnabled = _s.Host.Trim().Length > 0 && _s.Serial.Trim().Length > 0 && _s.Code.Trim().Length >= 4;
    }

    private void Rebuild()
    {
        // Re-render the page in place by asking the window to refresh.
        _ctx.Window.RefreshForced();
    }
}
