using Microsoft.Win32;
using Tomte.Core;
using Windows.UI.ViewManagement;

namespace Tomte.Shell;

/// <summary>
/// Keeps <see cref="Theme"/> on the taskbar's look and the user's choices: light or dark, and the accent on the taskbar.
/// Windows announces each change with a WM_SETTINGCHANGE to the island, so nothing here polls; every read runs on the
/// island's thread, where the views restyle. Simplification: the island stays solid when the taskbar is translucent,
/// since a blur of the desktop is not available to a window that never activates.
/// </summary>
internal static class TaskbarTheme
{
    private const string Personalize = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static void Follow(IslandWindow island, SettingsStore store, Action<string>? log = null)
    {
        var ui = new UISettings();
        void Apply()
        {
            var s = store.Current;
            // A missing value is Windows' own default: a dark taskbar without the accent.
            bool light = Read("SystemUsesLightTheme", 0) == 1;
            bool accentOnTaskbar = Read("ColorPrevalence", 0) == 1;
            Theme.UseScheme(
                Theme.SchemeFor(s.Theme, light, accentOnTaskbar),
                Argb(ui.GetColorValue(UIColorType.AccentDark2)),
                Argb(ui.GetColorValue(UIColorType.AccentLight2)));
            Theme.UseAccent(AccentColors.Resolve(s.AccentColor, Argb(ui.GetColorValue(UIColorType.Accent))));
            log?.Invoke($"theme {Theme.Scheme}, accent {Theme.Accent:X8}");
        }

        Apply();
        island.ColorsChanged = Apply;
        store.Changed += _ => island.Queue.TryEnqueue(() => Apply());
    }

    private static int Read(string name, int missing) => Registry.GetValue(Personalize, name, missing) as int? ?? missing;

    private static uint Argb(Windows.UI.Color c) => ShelfColors.Argb(c);
}
