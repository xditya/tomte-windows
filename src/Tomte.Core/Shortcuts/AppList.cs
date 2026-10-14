using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;

namespace Tomte.Core.Shortcuts;

/// <summary>An app as Start lists it: its name and its AppUserModelID, which is how Start opens it.</summary>
public sealed record InstalledApp(string Name, string Id)
{
    /// <summary>What a tile for this app stores, and the shell item its icon is read from.</summary>
    public string Target => ShortcutTarget.AppsFolder + Id;
}

/// <summary>
/// The apps in Start's list, read from the shell's Applications folder: desktop apps from the Start menu's
/// shortcuts and packaged (Store) apps alike. Reading it takes tens to hundreds of milliseconds and loads the
/// shell's app code, so call it from a worker thread, and only when the user asks for the list.
/// </summary>
public static class AppList
{
    /// <summary>Every app Start lists, sorted by name.</summary>
    public static IReadOnlyList<InstalledApp> Load() => Sorted(Items());

    /// <summary>The list as the picker shows it: entries with a name, sorted by it, ties kept in a fixed order.</summary>
    public static IReadOnlyList<InstalledApp> Sorted(IEnumerable<(string Name, string Id)> items) =>
        [.. items
            .Select(item => new InstalledApp(item.Name.Trim(), item.Id))
            .Where(app => app.Name.Length > 0 && ShortcutTarget.Refuse(app.Target) is null)
            .OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(app => app.Id, StringComparer.Ordinal)];

    /// <summary>The apps whose name holds the typed text, case ignored; all of them for an empty box.</summary>
    public static IEnumerable<InstalledApp> Filter(IEnumerable<InstalledApp> apps, string query) =>
        query.Trim() is { Length: > 0 } text ? apps.Where(app => app.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase)) : apps;

    private static unsafe List<(string Name, string Id)> Items()
    {
        var found = new List<(string, string)>();
        IShellItem? folder = null;
        IEnumShellItems? items = null;
        try
        {
            // By its shell name: SHGetKnownFolderItem is documented not to take a virtual folder such as this one.
            PInvoke.SHCreateItemFromParsingName("shell:AppsFolder", null, out folder).ThrowOnFailure();
            folder!.BindToHandler(null, PInvoke.BHID_EnumItems, out items);
            var one = new IShellItem[1];
            // S_FALSE ends the list; a failure ends it too, never reusing the item released last time round.
            for (uint fetched = 0; items!.Next(1, one, &fetched) == HRESULT.S_OK && fetched == 1; fetched = 0)
            {
                try
                {
                    // The name relative to the folder is the app's AppUserModelID.
                    found.Add((Name(one[0], SIGDN.SIGDN_NORMALDISPLAY), Name(one[0], SIGDN.SIGDN_PARENTRELATIVEPARSING)));
                }
                catch (Exception e) when (e is COMException or ArgumentException or IOException or UnauthorizedAccessException)
                {
                    // An entry the shell cannot name is left out.
                }
                finally
                {
                    Marshal.FinalReleaseComObject(one[0]);
                }
            }
        }
        finally
        {
            if (items is not null)
            {
                Marshal.FinalReleaseComObject(items);
            }

            if (folder is not null)
            {
                Marshal.FinalReleaseComObject(folder);
            }
        }

        return found;
    }

    private static unsafe string Name(IShellItem item, SIGDN form)
    {
        item.GetDisplayName(form, out PWSTR name);
        try
        {
            return name.ToString();
        }
        finally
        {
            PInvoke.CoTaskMemFree(name);
        }
    }
}
