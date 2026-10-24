using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace LuaToolsGui.Services;

/// <summary>Applies the saved AzaelX Shop appearance by replacing DynamicResource brushes only.
/// It intentionally does not touch view models, commands, download services, or Steam integrations.</summary>
public static class AppThemeService
{
    private sealed record Palette(
        string Surface, string Raised, string Sidebar, string Card, string Stroke,
        string Accent, string AccentStrong, string Tint, string Text, string Muted,
        string Success, string Warning, string Danger, string Pressed, string NavPointer, string NavSelected,
        string AccentText, string TextSecondary, string TextTertiary, string ControlStrokeSecondary);

    private static readonly Palette Dark = new(
        "#101525", "#161E31", "#080D18", "#111A2C", "#29364F",
        "#29D9F2", "#8A55FF", "#172A43", "#EAF4FF", "#91A4BF",
        "#47DBA0", "#FFC16D", "#FF7187", "#263D5E", "#17253B", "#222D4A",
        "#07111E", "#A9B8CE", "#91A4BF", "#394969");

    private static readonly Palette Light = new(
        "#FFFFFF", "#F4F8FD", "#F3F7FC", "#FFFFFF", "#D9E5F2",
        "#10AFCF", "#7250E8", "#E8F8FF", "#192B43", "#62758C",
        "#178553", "#A95A0A", "#CF3F60", "#DDF4FA", "#E8F6FC", "#D8EEF8",
        "#07111E", "#526A82", "#71849A", "#C7D9EA");

    public static void Apply(string? mode)
    {
        var app = Application.Current;
        if (app is null) return;

        bool dark = !string.Equals(mode, "Light", StringComparison.OrdinalIgnoreCase);
        Palette p = dark ? Dark : Light;

        // Keep native WPF-UI controls and the title bar in sync with the custom palette.
        try
        {
            ApplicationThemeManager.Apply(dark ? ApplicationTheme.Dark : ApplicationTheme.Light,
                WindowBackdropType.Mica, updateAccent: false);
        }
        catch { /* A theme manager failure must never interrupt startup or a settings change. */ }
        var colors = new Dictionary<string, string>
        {
            ["AppSurfaceBrush"] = p.Surface, ["AppSurfaceRaisedBrush"] = p.Raised,
            ["AppSidebarBrush"] = p.Sidebar, ["AppCardBrush"] = p.Card, ["AppStrokeBrush"] = p.Stroke,
            ["AppAccentBrush"] = p.Accent, ["AppAccentStrongBrush"] = p.AccentStrong,
            ["AppTintBrush"] = p.Tint, ["AppTextBrush"] = p.Text, ["AppMutedBrush"] = p.Muted,
            ["AppSuccessBrush"] = p.Success, ["AppWarningBrush"] = p.Warning, ["AppDangerBrush"] = p.Danger,
            ["AppPressedBrush"] = p.Pressed, ["AppNavPointerBrush"] = p.NavPointer, ["AppNavSelectedBrush"] = p.NavSelected,
            ["AccentFillColorDefaultBrush"] = p.Accent, ["AccentFillColorSecondaryBrush"] = p.AccentStrong,
            ["AccentFillColorTertiaryBrush"] = p.Tint, ["AccentTextFillColorPrimaryBrush"] = p.AccentText,
            ["TextFillColorPrimaryBrush"] = p.Text, ["TextFillColorSecondaryBrush"] = p.TextSecondary,
            ["TextFillColorTertiaryBrush"] = p.TextTertiary, ["ControlFillColorDefaultBrush"] = p.Raised,
            ["ControlFillColorSecondaryBrush"] = p.Tint, ["ControlStrokeColorDefaultBrush"] = p.Stroke,
            ["ControlStrokeColorSecondaryBrush"] = p.ControlStrokeSecondary, ["CardBackgroundFillColorDefaultBrush"] = p.Card,
            ["SolidBackgroundFillColorBaseBrush"] = p.Surface, ["SolidBackgroundFillColorSecondaryBrush"] = p.Raised,
            ["SubtleFillColorSecondaryBrush"] = p.Tint
        };
        foreach (var (key, value) in colors)
            app.Resources[key] = Brush(value);

        // Keep standard WPF ComboBox dropdowns in sync with Light/Dark mode.
        app.Resources[SystemColors.WindowBrushKey] =
            Brush(dark ? "#101525" : "#FFFFFF");

        app.Resources[SystemColors.WindowTextBrushKey] =
            Brush(dark ? "#EAF4FF" : "#192B43");

        app.Resources[SystemColors.ControlBrushKey] =
            Brush(dark ? "#161E31" : "#F4F8FD");

        app.Resources[SystemColors.ControlTextBrushKey] =
            Brush(dark ? "#EAF4FF" : "#192B43");

        app.Resources[SystemColors.HighlightBrushKey] =
            Brush(dark ? "#222D4A" : "#D8EEF8");

        app.Resources[SystemColors.HighlightTextBrushKey] =
            Brush(dark ? "#EAF4FF" : "#192B43");

        app.Resources["AppHeroBrush"] = dark
            ? Gradient(new[] { (0.0, "#11152B"), (0.52, "#172140"), (1.0, "#211737") })
            : Gradient(new[] { (0.0, "#E3F8FF"), (0.52, "#EEF0FF"), (1.0, "#FFFFFF") });
        app.Resources["AppBackgroundBrush"] = dark
            ? Gradient(new[] { (0.0, "#0B1020"), (0.55, "#0E1425"), (1.0, "#101525") })
            : Gradient(new[] { (0.0, "#F7FBFF"), (0.55, "#F1F7FD"), (1.0, "#EAF3FC") });

        // NavigationView keeps its palette in a local dictionary, so refresh those brushes too.
        foreach (Window window in app.Windows)
        {
            if (window is not MainWindow main || main.RootNavigation is null) continue;
            var local = main.RootNavigation.Resources;
            local["NavigationViewExpandedPaneBackground"] = Brush(p.Sidebar);
            local["NavigationViewDefaultPaneBackground"] = Brush(p.Sidebar);
            local["NavigationViewTopPaneBackground"] = Brush(p.Sidebar);
            local["NavigationViewItemBackgroundPointerOver"] = Brush(p.NavPointer);
            local["NavigationViewItemBackgroundChecked"] = Brush(p.NavSelected);
            local["NavigationViewItemBackgroundCheckedPointerOver"] = Brush(p.NavSelected);
            local["NavigationViewItemForeground"] = Brush(p.Muted);
            local["NavigationViewItemForegroundChecked"] = Brush(p.Accent);
        }
    }

    private static SolidColorBrush Brush(string value) => new((Color)ColorConverter.ConvertFromString(value));

    private static LinearGradientBrush Gradient((double Offset, string Color)[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        foreach (var (offset, color) in stops)
            brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(color), offset));
        return brush;
    }
}
