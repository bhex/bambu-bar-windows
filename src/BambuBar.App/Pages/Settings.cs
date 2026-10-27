using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BambuBar.Core.App;
using BambuBar.Core.I18n;
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
        root.Children.Add(PageKit.Header(Loc.T("set.title")));

        // MARK: printers
        var printers = new StackPanel();
        if (store.Sessions.Count == 0)
        {
            printers.Children.Add(Ui.Body(Loc.T("set.no_printer")));
        }
        else
        {
            foreach (var s in store.Sessions)
                printers.Children.Add(PrinterRow(s));
            if (store.Sessions.Count > 1)
                printers.Children.Add(Ui.Body(Loc.T("set.multi"), 11.5));
        }
        var addRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
        addRow.Children.Add(PageKit.Secondary(Loc.T("btn.add_printer"), () => _window.BeginAddPrinter()));
        printers.Children.Add(addRow);
        root.Children.Add(Group(Loc.T("group.printers"), printers));

        // MARK: the selected printer
        if (config is not null)
            root.Children.Add(Group(store.HasMultiplePrinters ? config.Name : Loc.T("group.printer"), PrinterGroup(session!, config)));

        // MARK: tray
        root.Children.Add(Group(Loc.T("group.tray"), TrayGroup(settings, store.Sessions)));

        // MARK: notifications
        var notifications = new StackPanel();
        notifications.Children.Add(Toggle(Loc.T("notif.finished"), settings.NotifyFinished, v => { settings.NotifyFinished = v; settings.Save(); }));
        notifications.Children.Add(Toggle(Loc.T("notif.failed"), settings.NotifyFailed, v => { settings.NotifyFailed = v; settings.Save(); }));
        notifications.Children.Add(Toggle(Loc.T("notif.paused"), settings.NotifyPaused, v => { settings.NotifyPaused = v; settings.Save(); }));
        notifications.Children.Add(Toggle(Loc.T("notif.started"), settings.NotifyStarted, v => { settings.NotifyStarted = v; settings.Save(); }));
        root.Children.Add(Group(Loc.T("group.notifications"), notifications));

        // MARK: controls
        var controls = new StackPanel();
        controls.Children.Add(Toggle(Loc.T("controls.confirm_stop"), settings.ConfirmStop, v => { settings.ConfirmStop = v; settings.Save(); }));
        controls.Children.Add(Ui.Body(
            Loc.T("controls.note"),
            11.5));
        root.Children.Add(Group(Loc.T("group.controls"), controls));

        // MARK: appearance
        var appearance = new StackPanel();
        var theme = new ComboBox { Width = 260, FontSize = 12.5, HorizontalAlignment = HorizontalAlignment.Left };
        string ThemeTitle(SettingsStore.ThemeMode m) => m switch
        {
            SettingsStore.ThemeMode.Light => Loc.T("theme.light"),
            SettingsStore.ThemeMode.Dark => Loc.T("theme.dark"),
            _ => Loc.T("theme.auto"),
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
        appearance.Children.Add(Row(Loc.T("row.theme"), theme));
        appearance.Children.Add(Ui.Body(
            Loc.T("theme.auto_desc"),
            11.5));

        var language = new ComboBox { Width = 260, FontSize = 12.5, HorizontalAlignment = HorizontalAlignment.Left };
        language.Items.Add(new LangChoice(Loc.T("lang.auto"), null));
        foreach (var code in Loc.Supported)
            language.Items.Add(new LangChoice(Loc.NativeNames[code], code));
        string? currentSetting = settings.LanguageSetting;
        for (int i = 0; i < language.Items.Count; i++)
        {
            var item = (LangChoice)language.Items[i];
            if (currentSetting is null ? item.Code is null : string.Equals(item.Code, currentSetting, StringComparison.OrdinalIgnoreCase))
            { language.SelectedIndex = i; break; }
        }
        language.SelectionChanged += (_, _) =>
        {
            if (language.SelectedItem is LangChoice choice)
            {
                settings.LanguageSetting = choice.Code;
                settings.Save();
                Loc.Set(choice.Code); // Loc.Changed re-renders both windows
            }
        };
        appearance.Children.Add(Row(Loc.T("row.language"), language));
        appearance.Children.Add(Ui.Body(
            Loc.T("lang.desc"),
            11.5));
        root.Children.Add(Group(Loc.T("group.appearance"), appearance));

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
                Text = "  ·  " + Loc.T("set.in_tray"),
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
            var button = PageKit.Secondary(isCurrent ? Loc.T("btn.showing") : Loc.T("btn.show"), () => _ctx.Store.Select(config.Serial));
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
        content.Children.Add(Row(Loc.T("row.name"), nameBox));

        content.Children.Add(Row(Loc.T("row.model"), Secondary("Bambu Lab " + config.ModelName)));
        content.Children.Add(Row(Loc.T("row.serial"), Secondary(config.Serial)));
        content.Children.Add(Row(Loc.T("row.status"), StatusDot(session)));

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
            content.Children.Add(Row(Loc.T("setup.ip"), hostBox));

            var codeBox = new PasswordBox
            {
                Password = _codeDraft,
                Width = 220,
                FontSize = 12.5,
                Padding = new Thickness(7, 5, 7, 5),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            codeBox.PasswordChanged += (_, _) => _codeDraft = codeBox.Password;
            content.Children.Add(Row(Loc.T("row.access_code"), codeBox));

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 8, 0, 0),
            };
            buttons.Children.Add(PageKit.Secondary(Loc.T("btn.cancel"), () => { _editingConnection = false; _hostDraft = ""; _codeDraft = ""; Rebuild(); }));
            bool canConnect = _hostDraft.Trim().Length > 0 && _codeDraft.Trim().Length >= 4;
            var reconnect = PageKit.Primary(Loc.T("btn.reconnect"), () => ApplyConnection(config), width: 130);
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
            var change = PageKit.Secondary(Loc.T("btn.change"), () =>
            {
                _hostDraft = config.Host;
                _codeDraft = "";
                _editingConnection = true;
                Rebuild();
            });
            ipRow.Children.Add(ipText);
            ipRow.Children.Add(change);
            content.Children.Add(Row(Loc.T("setup.ip"), ipRow));
        }

        var footer = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var note = new TextBlock
        {
            Text = Loc.T("set.ip_follow"),
            FontSize = 11.5,
            Foreground = Ui.Brush(Palette.Idle),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var forget = PageKit.Danger(Loc.T("btn.forget"), () =>
        {
            if (!PageKit.Confirm(
                    Loc.Tf("set.forget_confirm", config.Name)))
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
            combo.Items.Add(new TrayChoice(Loc.T("tray.auto"), null));
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
            content.Children.Add(Row(Loc.T("tray.printer"), combo));
            if (settings.MenuBarSerial is null)
                content.Children.Add(Ui.Body(
                    Loc.T("tray.auto_desc"),
                    11.5));
        }

        var style = new ComboBox { Width = 220, FontSize = 12.5, HorizontalAlignment = HorizontalAlignment.Left };
        string Title(SettingsStore.MenuBarStyle s) => s switch
        {
            SettingsStore.MenuBarStyle.Icon => Loc.T("tray.icon"),
            SettingsStore.MenuBarStyle.Percent => Loc.T("tray.percent"),
            SettingsStore.MenuBarStyle.Remaining => Loc.T("tray.remaining"),
            _ => Loc.T("tray.both"),
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
        content.Children.Add(Row(Loc.T("tray.show"), style));

        var login = new CheckBox
        {
            Content = Loc.T("tray.open_at_login"),
            IsChecked = AutoStart.IsEnabled,
            FontSize = 12.5,
        };
        login.Checked += (_, _) => { AutoStart.SetEnabled(true); settings.OpenAtLogin = true; settings.Save(); };
        login.Unchecked += (_, _) => { AutoStart.SetEnabled(false); settings.OpenAtLogin = false; settings.Save(); };
        content.Children.Add(Row("", login));

        content.Children.Add(Ui.Body(
            Loc.T("tray.login_desc"),
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
            case PrinterSession.Phase.Online: return Loc.T("link.connected");
            case PrinterSession.Phase.Connecting: return Loc.T("link.connecting");
            case PrinterSession.Phase.Offline: return session.PhaseReason ?? Loc.T("link.offline");
            case PrinterSession.Phase.Unauthorized: return Loc.T("link.unauthorized_short");
            case PrinterSession.Phase.Certificate: return Loc.T("link.cert");
            default: return session.PhaseReason ?? Loc.T("link.not_connected");
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

    private sealed record LangChoice(string Label, string? Code)
    {
        public override string ToString() => Label;
    }
}
