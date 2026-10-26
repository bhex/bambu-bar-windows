using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BambuBar.Core.App;
using BambuBar.Core.Models;
using BambuBar.Core.Sessions;
using BambuBar.Core.Store;
using Palette = BambuBar.App.Palette;

namespace BambuBar.App;

/// <summary>
/// Preferences (port of the macOS <c>SettingsPage</c>): the printers, the connection, the tray,
/// notifications and control behaviour. The macOS "Desktop widget" group has no Windows equivalent
/// and is deliberately dropped.
/// </summary>
public sealed class SettingsPage
{
    private readonly PageContext _ctx;
    private readonly MainWindow _window;

    // Draft state for the inline edits. The page is rebuilt on demand, so these persist across rebuilds.
    private string _nameDraft = "";
    private string _hostDraft = "";
    private string _codeDraft = "";
    private bool _editingConnection;
    private bool _nameEdited;

    public SettingsPage(PageContext ctx, MainWindow window)
    {
        _ctx = ctx;
        _window = window;
    }

    public FrameworkElement Build()
    {
        var store = _ctx.Store;
        var settings = store.Settings;
        var session = store.Current;
        var config = session?.Config;

        var root = new StackPanel
        {
            Margin = new Thickness(28, 24, 28, 24),
            MaxWidth = 860,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        root.Children.Add(PageKit.Header("Settings"));

        // MARK: printers
        var printers = new StackPanel();
        if (store.Sessions.Count == 0)
        {
            printers.Children.Add(Ui.Body("No printer set up yet."));
        }
        else
        {
            foreach (var s in store.Sessions)
                printers.Children.Add(PrinterRow(s));
            if (store.Sessions.Count > 1)
                printers.Children.Add(Ui.Body("Bambu Bar watches all of them at once.", 11.5));
        }
        var addRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
        addRow.Children.Add(PageKit.Secondary("Add Printer…", () => _window.BeginAddPrinter()));
        printers.Children.Add(addRow);
        root.Children.Add(Group("Printers", printers));

        // MARK: the selected printer
        if (config is not null)
            root.Children.Add(Group(store.HasMultiplePrinters ? config.Name : "Printer", PrinterGroup(session!, config)));

        // MARK: tray
        root.Children.Add(Group("Tray", TrayGroup(settings, store.Sessions)));

        // MARK: notifications
        var notifications = new StackPanel();
        notifications.Children.Add(Toggle("Print finished", settings.NotifyFinished, v => { settings.NotifyFinished = v; settings.Save(); }));
        notifications.Children.Add(Toggle("Print failed, canceled or an error", settings.NotifyFailed, v => { settings.NotifyFailed = v; settings.Save(); }));
        notifications.Children.Add(Toggle("Print paused (filament runout, user)", settings.NotifyPaused, v => { settings.NotifyPaused = v; settings.Save(); }));
        notifications.Children.Add(Toggle("Print started", settings.NotifyStarted, v => { settings.NotifyStarted = v; settings.Save(); }));
        root.Children.Add(Group("Notifications", notifications));

        // MARK: controls
        var controls = new StackPanel();
        controls.Children.Add(Toggle("Ask before stopping a print", settings.ConfirmStop, v => { settings.ConfirmStop = v; settings.Save(); }));
        controls.Children.Add(Ui.Body(
            "Pause, stop and speed changes need LAN Only mode with Developer Mode on the printer (Settings → Network on its screen). Reading status and the chamber light work in any mode.",
            11.5));
        root.Children.Add(Group("Controls", controls));

        // MARK: appearance
        var appearance = new StackPanel();
        var theme = new ComboBox { Width = 260, FontSize = 12.5, HorizontalAlignment = HorizontalAlignment.Left };
        string ThemeTitle(SettingsStore.ThemeMode m) => m switch
        {
            SettingsStore.ThemeMode.Light => "Light",
            SettingsStore.ThemeMode.Dark => "Dark",
            _ => "Automatic — follow Windows",
        };
        foreach (var m in new[] { SettingsStore.ThemeMode.Auto, SettingsStore.ThemeMode.Light, SettingsStore.ThemeMode.Dark })
            theme.Items.Add(new ThemeChoice(ThemeTitle(m), m));
        for (int i = 0; i < theme.Items.Count; i++)
            if (((ThemeChoice)theme.Items[i]).Value == settings.ThemeModeSetting) { theme.SelectedIndex = i; break; }
        theme.SelectionChanged += (_, _) =>
        {
            if (theme.SelectedItem is ThemeChoice choice)
            {
                settings.ThemeModeSetting = choice.Value;
                settings.Save();
                ThemeEngine.Current.SetMode(choice.Value);
            }
        };
        appearance.Children.Add(Row("Theme", theme));
        appearance.Children.Add(Ui.Body(
            "Automatic picks light or dark from the Windows system appearance and follows it live when you change it in Windows Settings → Personalization.",
            11.5));
        root.Children.Add(Group("Appearance", appearance));

        return root;
    }

    // MARK: groups

    private static UIElement Group(string title, UIElement content)
    {
        var block = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        block.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeEngine.Current.PrimaryBrush,
            Margin = new Thickness(0, 0, 0, 8),
        });
        var panel = Ui.Panel(content, radius: 16, padding: 16);
        block.Children.Add(panel);
        return block;
    }

    private UIElement PrinterRow(PrinterSession session)
    {
        var config = session.Config;
        if (config is null) return new Border();

        bool isCurrent = _ctx.Store.Current == session;
        bool online = session.CurrentPhase == PrinterSession.Phase.Online;

        var row = new Grid { Margin = new Thickness(0, 5, 0, 5) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel();
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
        nameRow.Children.Add(new TextBlock { Text = config.Name, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = ThemeEngine.Current.PrimaryBrush });
        if (_ctx.Store.HasMultiplePrinters && _ctx.Store.TraySession == session)
            nameRow.Children.Add(new TextBlock
            {
                Text = "  ·  in tray",
                FontSize = 11,
                Foreground = Ui.Brush(Palette.Idle),
            });
        left.Children.Add(nameRow);
        left.Children.Add(new TextBlock
        {
            Text = $"Bambu Lab {config.ModelName} · {config.Host}",
            FontSize = 11,
            Foreground = Ui.Brush(Palette.Idle),
        });
        Grid.SetColumn(left, 0);
        row.Children.Add(left);

        var status = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        status.Children.Add(new Border
        {
            Width = 7,
            Height = 7,
            CornerRadius = new CornerRadius(4),
            Background = Ui.Brush(online ? Palette.Accent : Palette.Paused),
            Margin = new Thickness(0, 0, 6, 0),
        });
        status.Children.Add(new TextBlock
        {
            Text = ShortState(session),
            FontSize = 11.5,
            Foreground = Ui.Brush(Palette.Idle),
            VerticalAlignment = VerticalAlignment.Center,
        });
        Grid.SetColumn(status, 1);
        row.Children.Add(status);

        if (_ctx.Store.HasMultiplePrinters)
        {
            var button = PageKit.Secondary(isCurrent ? "Showing" : "Show", () => _ctx.Store.Select(config.Serial));
            button.IsEnabled = !isCurrent;
            Grid.SetColumn(button, 2);
            row.Children.Add(button);
        }
        return row;
    }

    private UIElement PrinterGroup(PrinterSession session, PrinterConfig config)
    {
        var store = _ctx.Store;
        var settings = store.Settings;
        var content = new StackPanel();

        // Name (editable).
        _nameDraft = _nameEdited ? _nameDraft : config.Name;
        var nameBox = new TextBox
        {
            Text = _nameDraft,
            Width = 220,
            FontSize = 12.5,
            Padding = new Thickness(7, 5, 7, 5),
            Background = ThemeEngine.Current.InputBackground,
            Foreground = ThemeEngine.Current.PrimaryBrush,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        void CommitName()
        {
            var name = _nameDraft.Trim();
            if (name.Length == 0 || name == config.Name) return;
            store.Rename(config.Serial, name);
            settings.Save();
            _nameEdited = false;
            Rebuild();
        }
        nameBox.TextChanged += (_, _) => { _nameDraft = nameBox.Text; _nameEdited = true; };
        nameBox.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) CommitName(); };
        nameBox.LostFocus += (_, _) => CommitName();
        content.Children.Add(Row("Name", nameBox));

        content.Children.Add(Row("Model", Secondary("Bambu Lab " + config.ModelName)));
        content.Children.Add(Row("Serial number", Secondary(config.Serial)));
        content.Children.Add(Row("Status", StatusDot(session)));

        if (_editingConnection)
        {
            _hostDraft = _hostDraft.Length > 0 ? _hostDraft : config.Host;
            var hostBox = new TextBox
            {
                Text = _hostDraft,
                Width = 220,
                FontSize = 12.5,
                Padding = new Thickness(7, 5, 7, 5),
                Background = ThemeEngine.Current.InputBackground,
                Foreground = ThemeEngine.Current.PrimaryBrush,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            hostBox.TextChanged += (_, _) => _hostDraft = hostBox.Text;
            content.Children.Add(Row("IP address", hostBox));

            var codeBox = new PasswordBox
            {
                Password = _codeDraft,
                Width = 220,
                FontSize = 12.5,
                Padding = new Thickness(7, 5, 7, 5),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            codeBox.PasswordChanged += (_, _) => _codeDraft = codeBox.Password;
            content.Children.Add(Row("Access code", codeBox));

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 8, 0, 0),
            };
            buttons.Children.Add(PageKit.Secondary("Cancel", () => { _editingConnection = false; _hostDraft = ""; _codeDraft = ""; Rebuild(); }));
            bool canConnect = _hostDraft.Trim().Length > 0 && _codeDraft.Trim().Length >= 4;
            var reconnect = PageKit.Primary("Reconnect", () => ApplyConnection(config), width: 130);
            reconnect.IsEnabled = canConnect;
            buttons.Children.Add(reconnect);
            content.Children.Add(buttons);
        }
        else
        {
            var ipRow = new StackPanel { Orientation = Orientation.Horizontal };
            var ipText = new TextBlock
            {
                Text = config.Host,
                FontSize = 12.5,
                Foreground = Ui.Brush(Palette.Idle),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
            };
            var change = PageKit.Secondary("Change…", () =>
            {
                _hostDraft = config.Host;
                _codeDraft = "";
                _editingConnection = true;
                Rebuild();
            });
            ipRow.Children.Add(ipText);
            ipRow.Children.Add(change);
            content.Children.Add(Row("IP address", ipRow));
        }

        var footer = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var note = new TextBlock
        {
            Text = "Bambu Bar follows the printer if its IP address changes.",
            FontSize = 11.5,
            Foreground = Ui.Brush(Palette.Idle),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var forget = PageKit.Danger("Forget Printer…", () =>
        {
            if (!PageKit.Confirm(
                    $"Forget {config.Name}?\n\nBambu Bar disconnects and removes the access code from the Windows Credential Manager. Print history stays."))
                return;
            store.Forget(config.Serial);
            settings.Save();
            _editingConnection = false;
            _hostDraft = _codeDraft = _nameDraft = "";
            _nameEdited = false;
            Rebuild();
        });
        Grid.SetColumn(note, 0);
        Grid.SetColumn(forget, 1);
        footer.Children.Add(note);
        footer.Children.Add(forget);
        content.Children.Add(footer);
        return content;
    }

    private UIElement TrayGroup(SettingsStore settings, IReadOnlyList<PrinterSession> sessions)
    {
        var content = new StackPanel();

        if (sessions.Count > 1)
        {
            var combo = new ComboBox { Width = 220, FontSize = 12.5, HorizontalAlignment = HorizontalAlignment.Left };
            combo.Items.Add(new TrayChoice("Automatic", null));
            foreach (var session in sessions)
                if (session.Config is { } config)
                    combo.Items.Add(new TrayChoice(config.Name, config.Serial));
            int index = 0;
            if (settings.MenuBarSerial is { } serial)
            {
                for (int i = 0; i < combo.Items.Count; i++)
                    if (((TrayChoice)combo.Items[i]).Serial == serial) { index = i; break; }
            }
            combo.SelectedIndex = index;
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedItem is TrayChoice choice)
                {
                    settings.MenuBarSerial = choice.Serial;
                    settings.Save();
                }
            };
            content.Children.Add(Row("Printer in the tray", combo));
            if (settings.MenuBarSerial is null)
                content.Children.Add(Ui.Body(
                    "Automatic shows whichever printer is busy (paused first, then printing), otherwise the one you last looked at.",
                    11.5));
        }

        var style = new ComboBox { Width = 220, FontSize = 12.5, HorizontalAlignment = HorizontalAlignment.Left };
        string Title(SettingsStore.MenuBarStyle s) => s switch
        {
            SettingsStore.MenuBarStyle.Icon => "Icon only",
            SettingsStore.MenuBarStyle.Percent => "Progress",
            SettingsStore.MenuBarStyle.Remaining => "Time left",
            _ => "Progress and time left",
        };
        foreach (var s in new[] { SettingsStore.MenuBarStyle.Icon, SettingsStore.MenuBarStyle.Percent, SettingsStore.MenuBarStyle.Remaining, SettingsStore.MenuBarStyle.Both })
            style.Items.Add(new StyleChoice(Title(s), s));
        for (int i = 0; i < style.Items.Count; i++)
            if (((StyleChoice)style.Items[i]).Value == settings.MenuBarStyleSetting) { style.SelectedIndex = i; break; }
        style.SelectionChanged += (_, _) =>
        {
            if (style.SelectedItem is StyleChoice choice)
            {
                settings.MenuBarStyleSetting = choice.Value;
                settings.Save();
            }
        };
        content.Children.Add(Row("While printing, show", style));

        var login = new CheckBox
        {
            Content = "Open at login",
            IsChecked = AutoStart.IsEnabled,
            FontSize = 12.5,
        };
        login.Checked += (_, _) => { AutoStart.SetEnabled(true); settings.OpenAtLogin = true; settings.Save(); };
        login.Unchecked += (_, _) => { AutoStart.SetEnabled(false); settings.OpenAtLogin = false; settings.Save(); };
        content.Children.Add(Row("", login));

        content.Children.Add(Ui.Body(
            "At login Bambu Bar starts in the tray only, without a window. Open it from the tray to see this window.",
            11.5));
        return content;
    }

    // MARK: helpers

    private static TextBlock Secondary(string text) => new()
    {
        Text = text,
        FontSize = 12.5,
        Foreground = Ui.Brush(Palette.Idle),
        TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static UIElement StatusDot(PrinterSession session)
    {
        bool online = session.CurrentPhase == PrinterSession.Phase.Online;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new Border
        {
            Width = 7,
            Height = 7,
            CornerRadius = new CornerRadius(4),
            Background = Ui.Brush(online ? Palette.Accent : Palette.Paused),
            Margin = new Thickness(0, 2, 6, 0),
        });
        row.Children.Add(new TextBlock
        {
            Text = LinkSummary(session),
            FontSize = 12.5,
            Foreground = Ui.Brush(Palette.Idle),
            VerticalAlignment = VerticalAlignment.Center,
        });
        return row;
    }

    private static string LinkSummary(PrinterSession session)
    {
        switch (session.CurrentPhase)
        {
            case PrinterSession.Phase.Online: return "Connected";
            case PrinterSession.Phase.Connecting: return "Connecting…";
            case PrinterSession.Phase.Offline: return session.PhaseReason ?? "Offline";
            case PrinterSession.Phase.Unauthorized: return "Access code rejected";
            case PrinterSession.Phase.Certificate: return "Can't verify the printer";
            default: return session.PhaseReason ?? "Not connected";
        }
    }

    private static string ShortState(PrinterSession session)
    {
        if (session.LastStatus is { } status && session.CurrentPhase == PrinterSession.Phase.Online)
            return status.State.IsActive() ? $"{status.State.Title()} · {status.Progress}%" : status.StateTitle;
        return LinkSummary(session);
    }

    private static UIElement Row(string title, UIElement content)
    {
        var grid = new Grid { Margin = new Thickness(0, 5, 0, 5) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = new TextBlock
        {
            Text = title,
            FontSize = 12.5,
            Foreground = ThemeEngine.Current.PrimaryBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        };
        Grid.SetColumn(label, 0);
        grid.Children.Add(label);
        Grid.SetColumn(content, 1);
        grid.Children.Add(content);
        return grid;
    }

    private static CheckBox Toggle(string text, bool value, Action<bool> onChange)
    {
        var box = new CheckBox
        {
            Content = text,
            IsChecked = value,
            FontSize = 12.5,
            Margin = new Thickness(0, 4, 0, 4),
        };
        box.Checked += (_, _) => onChange(true);
        box.Unchecked += (_, _) => onChange(false);
        return box;
    }

    private void ApplyConnection(PrinterConfig config)
    {
        var store = _ctx.Store;
        var host = _hostDraft.Trim();
        var code = _codeDraft.Trim();
        if (host.Length == 0 || code.Length < 4) return;
        var updated = new PrinterConfig
        {
            Host = host,
            Serial = config.Serial,
            Name = config.Name,
            ModelCode = config.ModelCode,
        };
        store.Connect(updated, code);
        store.Settings.Save();
        _editingConnection = false;
        _hostDraft = _codeDraft = "";
        Rebuild();
    }

    private void Rebuild() => _window.RefreshForced();

    private sealed record TrayChoice(string Label, string? Serial)
    {
        public override string ToString() => Label;
    }

    private sealed record StyleChoice(string Label, SettingsStore.MenuBarStyle Value)
    {
        public override string ToString() => Label;
    }

    private sealed record ThemeChoice(string Label, SettingsStore.ThemeMode Value)
    {
        public override string ToString() => Label;
    }
}
