using RpfPreview.Core;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace RpfPreview.App;

internal static class AppTheme
{
    private sealed record Preference(bool DarkMode);
    private static string PreferencePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RpfPreview", "appearance.json");
    public static bool IsDark { get; private set; }

    public static void Initialize()
    {
        bool dark = false;
        try
        {
            var file = new FileInfo(LocalPath.Require(PreferencePath));
            if (file.Exists && file.Length <= 4096)
                dark = JsonSerializer.Deserialize<Preference>(File.ReadAllText(file.FullName))?.DarkMode == true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        Apply(dark);
    }

    public static bool Set(bool dark)
    {
        Apply(dark);
        string? temporary = null;
        try
        {
            string path = LocalPath.Require(PreferencePath);
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Preference(dark)));
            File.Move(temporary, path, true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        finally
        {
            try { if (temporary != null && File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void Apply(bool dark)
    {
        IsDark = dark;
        var resources = Application.Current.Resources;
        void Brush(string key, string light, string dim)
        {
            var value = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? dim : light));
            value.Freeze(); resources[key] = value;
        }
        Brush("AppBackground", "#F3F3F3", "#202124");
        Brush("AppSurface", "#FFFFFF", "#191C21");
        Brush("AppRaisedSurface", "#FAFAFA", "#252930");
        Brush("AppButton", "#F5F5F5", "#343B45");
        Brush("AppButtonHover", "#E8EEF5", "#465363");
        Brush("AppButtonPressed", "#D3E5F6", "#263749");
        Brush("AppText", "#202020", "#E7EAF0");
        Brush("AppMutedText", "#606060", "#B0B8C5");
        Brush("AppDisabledText", "#777777", "#939CAB");
        Brush("AppBorder", "#D2D2D2", "#3D444F");
        Brush("AppSplitter", "#E5E5E5", "#343B45");
        Brush("AppThumbnail", "#EEEEEE", "#292F38");
        Brush("AppWarningBackground", "#FFF8DC", "#3B321C");
        Brush("AppWarningBorder", "#E6D9A7", "#756336");
        Brush("AppWarningText", "#68541E", "#E5CE8A");
        Brush("AppAccent", "#0078D4", "#60B8FF");
        Brush("AppAccentText", "#FFFFFF", "#102235");
        Application.Current.ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light;
    }
}
