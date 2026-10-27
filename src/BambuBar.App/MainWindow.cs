using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using BambuBar.Core.App;
using BambuBar.Core.I18n;
using BambuBar.Core.Models;
using BambuBar.Core.Sessions;
using Palette = BambuBar.App.Palette;

namespace BambuBar.App;

/// <summary>
/// The full window (port of the macOS <c>MainWindow</c>): a sidebar with the sections — Printer,
/// Filament, Settings, About — and the live state pinned to the bottom, like the macOS sidebar footer.
/// </summary>
public sealed class MainWindow : Window
{
    public const string SectionPrinter = "printer";
    public const string SectionFilament = "filament";
    public const string SectionSettings = "settings";
    public const string SectionAbout = "about";

    private readonly PrinterStore _store;
    private readonly TrayController _tray;
    private readonly ContentControl _pageHost;
    private readonly StackPanel _sidebarNav;
    private readonly DockPanel _sidebar;
    private TextBlock _footerName, _footerLine;
    private string _section = SectionPrinter;
    private bool _addingPrinter;
    private string? _lastKey;

    /// <summary>
    /// The setup draft, persisted across page rebuilds. <c>Refresh</c> recreates the page
    /// (and thus its local state) whenever the store changes, so anything the user is
    /// mid-entry on must live here, or a single click/keystroke would wipe it.
    /// </summary>
    public SetupState Setup = new();

    public MainWindow(PrinterStore store, TrayController tray, Action openPanel, Action quit)
    {
        _store = store;
        _tray = tray;

        Title = "Bambu Bar";
        Width = 900;
        Height = 640;
        MinWidth = 820;
        MinHeight = 600;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = ThemeEngine.Current.WindowBackground;
        Icon = TrayIcon.AppImage();

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // MARK: sidebar
        _sidebar = new DockPanel { Background = ThemeEngine.Current.SidebarBackground };
        _footerName = new TextBlock();
        _footerLine = new TextBlock();
        DockPanel.SetDock(footer(), Dock.Bottom);
        _sidebar.Children.Add(footer());

        _sidebarNav = new StackPanel { Margin = new Thickness(10, 14, 10, 0) };
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _sidebarNav };
        DockPanel.SetDock(scroll, Dock.Top);
        _sidebar.Children.Add(scroll);

        grid.Children.Add(_sidebar);
        Grid.SetColumn(_sidebar, 0);

        // MARK: page host
        _pageHost = new ContentControl();
        var pageScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _pageHost,
        };
        grid.Children.Add(pageScroll);
        Grid.SetColumn(pageScroll, 1);

        Content = grid;

        BuildNav();
        _store.Changed += () => Dispatcher.BeginInvoke(() => Refresh());
        ThemeEngine.Current.Changed += ApplyTheme;
        Loc.Changed += () => Dispatcher.BeginInvoke(() => { BuildNav(); Refresh(force: true); });
        Closed += (_, _) => ThemeEngine.Current.Changed -= ApplyTheme;
        Refresh(force: true);
    }

    /// <summary>Re-apply the theme to the live chrome and rebuild the page (pages build colors at creation time).</summary>
    private void ApplyTheme()
    {
        Background = ThemeEngine.Current.WindowBackground;
        _sidebar.Background = ThemeEngine.Current.SidebarBackground;
        _footerName.Foreground = ThemeEngine.Current.PrimaryBrush;
        BuildNav();
        Refresh(force: true);
    }

    // MARK: Navigation

    private UIElement footer()
    {
        var content = new StackPanel { Margin = new Thickness(14, 10, 14, 12) };
        _footerName = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = ThemeEngine.Current.PrimaryBrush, TextTrimming = TextTrimming.CharacterEllipsis };
        _footerLine = new TextBlock { FontSize = 10.5, Foreground = Ui.Brush(Palette.Idle), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) };
        content.Children.Add(_footerName);
        content.Children.Add(_footerLine);
        return Ui.Panel(content, radius: 12, padding: 10);
    }

    private void BuildNav()
    {
        _sidebarNav.Children.Clear();
        _sidebarNav.Children.Add(NavItem(Loc.T("nav.printer"), _section == SectionPrinter, () => Navigate(SectionPrinter)));
        _sidebarNav.Children.Add(NavItem(Loc.T("nav.filament"), _section == SectionFilament, () => Navigate(SectionFilament)));
        _sidebarNav.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(ThemeEngine.Current.Neutral(0x22)), Margin = new Thickness(6, 10, 6, 10) });
        _sidebarNav.Children.Add(NavItem(Loc.T("nav.settings"), _section == SectionSettings, () => Navigate(SectionSettings)));
        _sidebarNav.Children.Add(NavItem(Loc.T("nav.about"), _section == SectionAbout, () => Navigate(SectionAbout)));
    }

    private UIElement NavItem(string title, bool selected, Action action)
    {
        var button = new Button
        {
            Content = new TextBlock
            {
                Text = title,
                FontSize = 13,
                FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = selected ? Ui.Brush(Palette.AccentDeep) : ThemeEngine.Current.NavTextBrush,
                HorizontalAlignment = HorizontalAlignment.Left,
            },
            Background = selected ? new SolidColorBrush(Color.FromArgb(0x22, Palette.Accent.R, Palette.Accent.G, Palette.Accent.B)) : Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 2, 0, 2),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        button.Click += (_, _) => action();
        return button;
    }

    public void Navigate(string section)
    {
        _section = section;
        _addingPrinter = false;
        BuildNav();
        Refresh(force: true);
    }

    public void BeginAddPrinter()
    {
        _section = SectionPrinter;
        _addingPrinter = true;
        BuildNav();
        Refresh(force: true);
    }

    /// <summary>Re-render the current page immediately (pages call this after a user action).</summary>
    public void RefreshForced() => Refresh(force: true);

    // MARK: Refresh

    private void Refresh(bool force = false)
    {
        var session = _store.Current;
        var status = session?.LastStatus;
        bool online = session is not null && session.CurrentPhase == PrinterSession.Phase.Online;

        // Sidebar footer (cheap to update always).
        if (_store.Sessions.Count == 0)
        {
            _footerName.Text = Loc.T("panel.no_printer");
            _footerLine.Text = Loc.T("mw.setup_cta");
        }
        else
        {
            _footerName.Text = session?.Config.Name ?? "";
            if (online && status is not null)
            {
                _footerLine.Text = status.State.IsActive() ? $"{status.State.Title()} · {status.Progress}%" : status.StateTitle;
            }
            else
            {
                _footerLine.Text = status is null ? (session?.PhaseReason ?? Loc.T("link.connecting")) : Loc.T("link.offline");
            }
        }

        // Page content: rebuild when the structure changes, otherwise update the live bits.
        string key = $"{_section}|{_addingPrinter}|{_store.Sessions.Count}|{session?.Config.Serial}|{session?.CurrentPhase}|{status?.State}|{status?.Issues.Count}|{status?.Ams.Count}|{status?.JobName}";
        if (!force && key == _lastKey) return;
        _lastKey = key;

        var ctx = new PageContext(_store, this, _tray);
        FrameworkElement page = _section switch
        {
            SectionPrinter => (_store.Sessions.Count == 0 || _addingPrinter)
                ? new SetupPage(ctx).Build()
                : new OverviewPage(ctx).Build(),
            SectionFilament => new FilamentPage(ctx).Build(),
            SectionSettings => new SettingsPage(ctx, this).Build(),
            _ => new AboutPage().Build(),
        };
        _pageHost.Content = page;
    }
}

/// <summary>The "add printer" draft shared by every incarnation of the setup page.</summary>
public sealed class SetupState
{
    public string Host = "";
    public string Serial = "";
    public string Name = "";
    public string ModelCode = "N7";
    public string? Selected;
    public string Code = "";
    public string? CodeSource;
    public List<string> AlternativeCodes = new();
    public string? ImportNote;
    public bool OpenAtLogin = true;
}

/// <summary>What a page can do to the world.</summary>
public sealed record PageContext(PrinterStore Store, MainWindow Window, TrayController Tray);
