using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Palette = BambuBar.App.Palette;

namespace BambuBar.App;

/// <summary>Shared page furniture (the macOS <c>PageHeader</c> and buttons).</summary>
public static class PageKit
{
    public static UIElement Header(string title, string? subtitle = null, UIElement? trailing = null)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel();
        left.Children.Add(new TextBlock { Text = title, FontSize = 26, FontWeight = FontWeights.Bold, Foreground = ThemeEngine.Current.PrimaryBrush });
        if (subtitle is { Length: > 0 })
            left.Children.Add(new TextBlock { Text = subtitle, FontSize = 13, Foreground = Ui.Brush(Palette.Idle), Margin = new Thickness(0, 3, 0, 0) });
        Grid.SetColumn(left, 0);
        grid.Children.Add(left);
        if (trailing is not null)
        {
            Grid.SetColumn(trailing, 1);
            if (trailing is FrameworkElement fe) fe.Margin = new Thickness(0, 0, 0, 4);
            grid.Children.Add(trailing);
        }
        return grid;
    }

    public static Button Primary(string text, Action action, double width = double.NaN)
    {
        var button = new Button
        {
            Content = text,
            Foreground = Brushes.White,
            Background = Ui.Brush(Palette.AccentDeep),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(14, 8, 14, 8),
            Margin = new Thickness(0, 0, 8, 0),
            FontSize = 12.5,
            FontWeight = FontWeights.Medium,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        if (!double.IsNaN(width)) button.Width = width;
        button.Click += (_, _) => action();
        return button;
    }

    public static Button Secondary(string text, Action action)
    {
        var button = new Button
        {
            Content = text,
            Foreground = ThemeEngine.Current.NavTextBrush,
            Background = new SolidColorBrush(ThemeEngine.Current.Neutral(0x14)),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(12, 7, 12, 7),
            Margin = new Thickness(0, 0, 8, 0),
            FontSize = 12,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        button.Click += (_, _) => action();
        return button;
    }

    public static Button Danger(string text, Action action)
    {
        var button = Secondary(text, action);
        button.Foreground = Ui.Brush(Palette.Failed);
        button.Background = new SolidColorBrush(Color.FromArgb(0x1F, Palette.Failed.R, Palette.Failed.G, Palette.Failed.B));
        return button;
    }

    public static Button Icon(string glyph, string help, Action action)
    {
        var button = new Button
        {
            Content = glyph,
            Width = 32,
            Height = 32,
            Foreground = Ui.Brush(Palette.Idle),
            Background = new SolidColorBrush(ThemeEngine.Current.Neutral(0x0D)),
            BorderThickness = new Thickness(0),
            FontSize = 14,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = help,
        };
        button.Click += (_, _) => action();
        return button;
    }

    public static bool Confirm(string message) =>
        MessageBox.Show(message, "Bambu Bar", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
}
