using System.Diagnostics;
using Tomte.Core.Device;

namespace Tomte.Core.Search;

/// <summary>
/// The places the search box finds besides apps and files, all from tables in this file: Tomte's settings window and its
/// pages, the island's tabs, Windows Settings pages (opened by their ms-settings: link) and a few Windows tools Start's
/// list lacks. Each has its name, where it is, and the words people type for it. Nothing is read from the system but
/// whether a tool's file exists, so a page this Windows does not have still shows; Windows opens Settings' home for it.
/// </summary>
public static class Places
{
    /// <summary>The Settings app, as Start lists it: the icon of every Windows Settings page.</summary>
    public const string SettingsApp = @"shell:AppsFolder\windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel";

    private const string SettingsUri = "ms-settings:", TomteSettings = "tomte:settings", TomteSection = "tomte:settings:", TomteTab = "tomte:tab:";

    // Tomte's settings window: its pages as the page list names them, then a few sections by their headers. A section
    // name has to match the window's own text, which a test checks. Shown name, header to open at, words.
    private static readonly (string Name, string Section, string Words)[] TomtePages =
    [
        ("General", "General", "start with windows startup desktop peek track changes monitors island position"),
        ("Opening and closing", "Opening and closing", "edge push dwell reveal drag reveal retract delay open close"),
        ("Hotkeys", "Hotkeys", "shortcut keys keyboard shortcuts key bindings hotkey"),
        ("Search", "Search", "file search everything windows index whole disk files web search engine"),
        ("Shelf and shortcuts", "Shelf and shortcuts", "shelf tiles drag out"),
        ("Clipboard", "Clipboard", "clipboard history keep history memory privacy"),
        ("Voice", "Voice", "rambler speech dictation spoken replies microphone"),
        ("AI", "AI", "provider api key model openai anthropic claude gemini ollama"),
        ("AI tools", "AI tools", "tools permissions always allowed desktop automation"),
        ("Per-app rules", "Per-app rules", "app rules fullscreen allow deny games"),
        ("AI provider", "AI provider", "api key model provider"),
        ("AI history", "AI history", "conversations answers keep history memory privacy"),
        ("Scheduled jobs", "Scheduled jobs", "jobs schedule recurring"),
        ("MCP servers", "MCP servers", "mcp server"),
        ("Skills", "Skills", "agent skills"),
    ];

    // The island's tabs: the words for each.
    private static readonly (PanelId Tab, string Words)[] Tabs =
    [
        (PanelId.Shelf, "files drop shelf"),
        (PanelId.Shortcuts, "tiles launcher"),
        (PanelId.Media, "music now playing player song"),
        (PanelId.Scratchpad, "notes note scratch pad"),
        (PanelId.Clipboard, "copy paste copied history"),
        (PanelId.History, "ai history answers conversations"),
    ];

    // Windows Settings pages: the name as Settings shows it, the part of Settings it is in (empty for a part's own page),
    // the page after ms-settings:, and the words people type for it. Only pages with a documented link.
    private static readonly (string Name, string Area, string Page, string Words)[] Pages =
    [
        ("Display", "System", "display", "screen resolution brightness monitor scale scaling zoom size orientation rotate landscape portrait hdr refresh rate hz multiple displays second monitor extend duplicate"),
        ("Night light", "System", "nightlight", "blue light warm screen night mode eye strain"),
        ("Graphics", "System", "display-advancedgraphics", "gpu graphics card preference high performance power saving"),
        ("Sound", "System", "sound", "audio volume speakers headphones output input microphone mic"),
        ("Volume mixer", "System", "apps-volume", "app volume per app audio loudness"),
        ("All sound devices", "System", "sound-devices", "audio devices speakers headphones disable"),
        ("Notifications", "System", "notifications", "alerts banners toasts do not disturb"),
        ("Focus", "System", "quiethours", "focus session focus assist do not disturb quiet hours"),
        ("Power & battery", "System", "powersleep", "sleep screen timeout turn off display power mode energy saver battery saver hibernate lid"),
        ("Battery usage", "System", "batterysaver-usagedetails", "battery life drain apps"),
        ("Storage", "System", "storagesense", "disk space free up space drive full cleanup temporary files"),
        ("Storage Sense", "System", "storagepolicies", "automatic cleanup recycle bin downloads"),
        ("Disks & volumes", "System", "disksandvolumes", "partitions drives format rename drive letter"),
        ("Default save locations", "System", "savelocations", "where new content is saved save location"),
        ("Nearby sharing", "System", "crossdevice", "share nearby files send to pc"),
        ("Multitasking", "System", "multitasking", "snap windows snap layouts alt tab virtual desktops title bar shake"),
        ("Activation", "System", "activation", "product key license activate windows edition upgrade"),
        ("Troubleshoot", "System", "troubleshoot", "troubleshooters fix problems"),
        ("Recovery", "System", "recovery", "reset this pc reinstall windows fresh start go back advanced startup safe mode"),
        ("Projecting to this PC", "System", "project", "miracast wireless display cast to this pc"),
        ("Remote Desktop", "System", "remotedesktop", "rdp remote access remote control"),
        ("Clipboard", "System", "clipboard", "clipboard history sync across devices copy paste"),
        ("About", "System", "about", "pc name rename this pc device specifications specs windows version build processor cpu ram memory system info product id"),
        ("Optional features", "System", "optionalfeatures", "add a feature windows features"),
        ("For developers", "System", "developers", "developer mode sudo dev drive powershell execution policy"),

        ("Bluetooth & devices", "", "bluetooth", "bluetooth bt pair add a device headphones earbuds speaker controller connect wireless"),
        ("Printers & scanners", "Bluetooth & devices", "printers", "printer scanner print add printer print queue"),
        ("Mouse", "Bluetooth & devices", "mousetouchpad", "pointer speed scroll wheel primary button cursor speed left handed"),
        ("Touchpad", "Bluetooth & devices", "devices-touchpad", "trackpad gestures tap to click scrolling three finger"),
        ("Pen & Windows Ink", "Bluetooth & devices", "pen", "stylus pen handwriting ink"),
        ("AutoPlay", "Bluetooth & devices", "autoplay", "usb drive memory card removable"),
        ("USB", "Bluetooth & devices", "usb", "usb connection notifications"),
        ("Cameras", "Bluetooth & devices", "camera", "webcam camera"),
        ("Phone Link", "Bluetooth & devices", "mobile-devices", "phone android iphone mobile devices"),

        ("Network & internet", "", "network-status", "internet connection network status wifi ethernet online offline"),
        ("Wi-Fi", "Network & internet", "network-wifi", "wifi wireless wlan networks connect"),
        ("Known networks", "Network & internet", "network-wifisettings", "manage known networks forget wifi saved networks"),
        ("Ethernet", "Network & internet", "network-ethernet", "wired lan cable ip address dns metered"),
        ("VPN", "Network & internet", "network-vpn", "vpn connection add vpn"),
        ("Mobile hotspot", "Network & internet", "network-mobilehotspot", "hotspot tethering share internet"),
        ("Airplane mode", "Network & internet", "network-airplanemode", "flight mode airplane"),
        ("Proxy", "Network & internet", "network-proxy", "proxy server pac script"),
        ("Advanced network settings", "Network & internet", "network-advancedsettings", "network adapters network reset data usage hardware properties"),

        ("Personalization", "", "personalization", "customize look appearance"),
        ("Background", "Personalization", "personalization-background", "wallpaper desktop background picture slideshow spotlight"),
        ("Colors", "Personalization", "personalization-colors", "dark mode light mode accent color transparency theme color"),
        ("Themes", "Personalization", "themes", "theme desktop icons sounds mouse cursor"),
        ("Lock screen", "Personalization", "lockscreen", "lock screen picture spotlight screen saver"),
        ("Touch keyboard", "Personalization", "personalization-touchkeyboard", "on screen keyboard size theme"),
        ("Start", "Personalization", "personalization-start", "start menu pins recommended layout recently added"),
        ("Folders on Start", "Personalization", "personalization-start-places", "start menu folders next to power button"),
        ("Taskbar", "Personalization", "taskbar", "taskbar alignment center left system tray icons widgets search box task view combine buttons hide"),
        ("Fonts", "Personalization", "fonts", "install fonts font"),
        ("Text input", "Personalization", "personalization-textinput", "emoji panel voice typing theme ime"),

        ("Installed apps", "Apps", "appsfeatures", "uninstall remove programs apps and features apps list"),
        ("Default apps", "Apps", "defaultapps", "default browser open with file associations file types"),
        ("Startup", "Apps", "startupapps", "startup apps programs run at startup boot sign in"),
        ("Video playback", "Apps", "videoplayback", "hdr video video quality"),

        ("Your info", "Accounts", "yourinfo", "account microsoft account profile picture account picture"),
        ("Sign-in options", "Accounts", "signinoptions", "password pin windows hello face fingerprint security key dynamic lock sign in login"),
        ("Email & accounts", "Accounts", "emailandaccounts", "email accounts add account"),
        ("Family", "Accounts", "family-group", "family parental controls kids child"),
        ("Other users", "Accounts", "otherusers", "add user add account other people guest local account"),
        ("Windows backup", "Accounts", "sync", "backup sync onedrive remember my apps preferences"),
        ("Access work or school", "Accounts", "workplace", "work school account mdm"),

        ("Date & time", "Time & language", "dateandtime", "clock time zone set time automatically sync now date"),
        ("Language & region", "Time & language", "regionlanguage", "language display language keyboard layout input country region locale"),
        ("Typing", "Time & language", "typing", "autocorrect spell check spelling text suggestions keyboard"),
        ("Speech", "Time & language", "speech", "voice speech language voices text to speech"),

        ("Game Bar", "Gaming", "gaming-gamebar", "xbox game bar overlay controller"),
        ("Captures", "Gaming", "gaming-gamedvr", "game recording record clips screenshots game dvr"),
        ("Game Mode", "Gaming", "gaming-gamemode", "game mode performance"),

        ("Text size", "Accessibility", "easeofaccess-display", "font size bigger text larger text make text bigger"),
        ("Visual effects", "Accessibility", "easeofaccess-visualeffects", "animations transparency scrollbars always show dismiss notifications"),
        ("Mouse pointer and touch", "Accessibility", "easeofaccess-mousepointer", "cursor size pointer size pointer color"),
        ("Text cursor", "Accessibility", "easeofaccess-cursor", "caret cursor thickness text cursor indicator"),
        ("Magnifier", "Accessibility", "easeofaccess-magnifier", "zoom magnify"),
        ("Color filters", "Accessibility", "easeofaccess-colorfilter", "color blind colorblind grayscale invert"),
        ("Contrast themes", "Accessibility", "easeofaccess-highcontrast", "high contrast"),
        ("Narrator", "Accessibility", "easeofaccess-narrator", "screen reader read aloud"),
        ("Audio", "Accessibility", "easeofaccess-audio", "mono audio flash screen notifications"),
        ("Captions", "Accessibility", "easeofaccess-closedcaptioning", "subtitles closed captions caption style"),
        ("Keyboard", "Accessibility", "easeofaccess-keyboard", "sticky keys filter keys toggle keys on screen keyboard underline access keys"),
        ("Mouse keys", "Accessibility", "easeofaccess-mouse", "mouse keys numeric keypad numpad move pointer"),

        ("Privacy & security", "", "privacy", "privacy permissions general advertising id"),
        ("Windows Security", "Privacy & security", "windowsdefender", "antivirus defender virus protection firewall security"),
        ("Device encryption", "Privacy & security", "deviceencryption", "bitlocker encrypt drive"),
        ("Find my device", "Privacy & security", "findmydevice", "lost stolen locate"),
        ("Camera", "Privacy & security", "privacy-webcam", "camera access webcam permission apps"),
        ("Microphone", "Privacy & security", "privacy-microphone", "mic access permission apps"),
        ("Location", "Privacy & security", "privacy-location", "gps location services location access"),
        ("Diagnostics & feedback", "Privacy & security", "privacy-feedback", "telemetry diagnostic data feedback frequency"),
        ("Activity history", "Privacy & security", "privacy-activityhistory", "timeline activity"),
        ("Search permissions", "Privacy & security", "search-permissions", "safesearch safe search cloud search history"),
        ("Searching Windows", "Privacy & security", "search-moredetails", "indexing index search index locations enhanced classic"),

        ("Windows Update", "", "windowsupdate", "updates check for updates update windows patches"),
        ("Update history", "Windows Update", "windowsupdate-history", "installed updates uninstall updates"),
        ("Advanced options", "Windows Update", "windowsupdate-options", "active hours optional updates delivery optimization restart"),
        ("Windows Insider Program", "Windows Update", "windowsinsider", "insider preview builds dev beta channel"),
    ];

    // Windows tools Start's list lacks on many PCs: the name, the program in System32 with its fixed arguments, the
    // words people type, and a file to take the icon from when the program's own is not it. Listed only when the program
    // is there (the Group Policy Editor is not on Home).
    private static readonly (string Name, string File, string Arguments, string Words, string? Icon)[] Tools =
    [
        ("Device Manager", "devmgmt.msc", "", "drivers hardware devices driver update", null),
        ("Disk Management", "diskmgmt.msc", "", "partitions partition drives format volume disks", null),
        ("Computer Management", "compmgmt.msc", "", "manage computer", null),
        ("Environment variables", "rundll32.exe", "sysdm.cpl,EditEnvironmentVariables", "env path variables edit environment for your account user", "sysdm.cpl"),
        ("Advanced system settings", "SystemPropertiesAdvanced.exe", "", "system properties system environment variables performance virtual memory startup and recovery user profiles", null),
        ("Network connections", "ncpa.cpl", "", "network adapters adapter options ethernet ipv4 ip address dns", null),
        ("Programs and Features", "appwiz.cpl", "", "uninstall a program add remove programs control panel", null),
        ("Sound control panel", "mmsys.cpl", "", "playback recording devices sound devices classic", null),
        ("Power Options", "powercfg.cpl", "", "power plan high performance balanced lid close sleep", null),
        ("Mouse properties", "main.cpl", "", "pointer double click speed cursor scheme", null),
        ("Internet Options", "inetcpl.cpl", "", "internet properties proxy lan", null),
        ("User Accounts", "netplwiz.exe", "", "sign in automatically auto login users netplwiz", null),
        ("Credential Manager", "control.exe", "/name Microsoft.CredentialManager", "saved passwords credentials windows credentials", null),
        ("Windows Features", "optionalfeatures.exe", "", "turn windows features on or off hyper-v wsl sandbox net framework", null),
        ("DirectX Diagnostic Tool", "dxdiag.exe", "", "dxdiag directx graphics card gpu", null),
        ("Display color calibration", "dccw.exe", "", "calibrate color gamma", null),
        ("ClearType Text Tuner", "cttune.exe", "", "cleartype font smoothing text sharpness", null),
        ("System Protection", "SystemPropertiesProtection.exe", "", "system restore restore point create a restore point", null),
        ("Performance Options", "SystemPropertiesPerformance.exe", "", "visual effects animations best performance virtual memory paging file", null),
        ("Computer name", "SystemPropertiesComputerName.exe", "", "rename computer workgroup domain", null),
        ("Group Policy Editor", "gpedit.msc", "", "group policy gpedit local policy", null),
        ("Local Users and Groups", "lusrmgr.msc", "", "users groups lusrmgr administrators", null),
        ("Certificate Manager", "certmgr.msc", "", "certificates user certificates", null),
    ];

    /// <summary>
    /// Every place as a search row, in this order: Tomte's settings, the island's tabs, Tomte's settings pages, Windows
    /// Settings pages, then the tools that are on this PC and that <paramref name="apps"/> (Start's list) does not already
    /// have, by name or by program. Ties in the ranking keep this order, so "clipboard" lists Tomte's Clipboard tab first.
    /// </summary>
    public static IReadOnlyList<SearchItem> Rows(IEnumerable<SearchItem> apps)
    {
        var listed = apps.ToList();
        string tomte = Environment.ProcessPath ?? "";
        var rows = new List<SearchItem> { new(SearchKind.Place, "Tomte settings", TomteSettings, "", tomte, Words: "tomte preferences options configure") };
        rows.AddRange(Tabs.Select(t => new SearchItem(SearchKind.Place, TabStrip.Name(t.Tab), TomteTab + t.Tab, "Tomte island", tomte, Words: "tomte tab " + t.Words)));
        rows.AddRange(TomtePages.Select(p => new SearchItem(SearchKind.Place, p.Name, TomteSection + p.Section, "Tomte settings", tomte, Words: "tomte " + p.Words)));
        rows.AddRange(Pages.Select(p => new SearchItem(SearchKind.Place, p.Name, SettingsUri + p.Page, p.Area, SettingsApp, Words: p.Area + " " + p.Words)));
        foreach (var tool in Tools)
        {
            string file = Path.Combine(Environment.SystemDirectory, tool.File);
            bool inStart = listed.Any(app => app.Name.Equals(tool.Name, StringComparison.CurrentCultureIgnoreCase)
                || (tool.Arguments.Length == 0 && app.Target.EndsWith(@"\" + tool.File, StringComparison.OrdinalIgnoreCase)));
            if (!inStart && File.Exists(file))
            {
                rows.Add(new SearchItem(SearchKind.Place, tool.Name, ToolTarget(tool), "Windows tool", tool.Icon is { } icon ? Path.Combine(Environment.SystemDirectory, icon) : file, Words: tool.Words));
            }
        }

        return rows;
    }

    /// <summary>
    /// How well a place matches: its name with the full fuzzy matcher, or its name, where it is and its words together by
    /// the starts of words only (initials, letters in order and typos over a long list of words would match nearly
    /// anything). For a settings page, the words "settings" and "setting" count as matched, so "display settings" is Display.
    /// </summary>
    public static int Score(SearchItem place, string query)
    {
        int best = Best(place, query);
        return IsSettingsPage(place.Target) && WithoutSettings(query) is { } rest ? Math.Max(best, Best(place, rest)) : best;
    }

    /// <summary>The words people type for one of Tomte's settings pages or sections, by its title or header; "" for none.</summary>
    public static string TomteWords(string section) => TomtePages.FirstOrDefault(p => p.Section == section).Words ?? "";

    /// <summary>The word at the row's right end: Settings for a Windows page, Tomte for Tomte's own, Tool for a tool.</summary>
    public static string Label(string target) =>
        target.StartsWith(SettingsUri, StringComparison.OrdinalIgnoreCase) ? "Settings"
            : target.StartsWith(TomteSettings, StringComparison.Ordinal) || target.StartsWith(TomteTab, StringComparison.Ordinal) ? "Tomte"
            : "Tool";

    /// <summary>The island tab a row opens, or null when it is not one.</summary>
    public static PanelId? Tab(string target) =>
        target.StartsWith(TomteTab, StringComparison.Ordinal) && Enum.TryParse<PanelId>(target[TomteTab.Length..], out var tab) && Enum.IsDefined(tab)
            ? tab : null;

    /// <summary>
    /// True when the row opens Tomte's settings window; <paramref name="section"/> is the page or section header to open
    /// at, null for the first page.
    /// </summary>
    public static bool TomteSettingsAt(string target, out string? section)
    {
        string? at = section = target.StartsWith(TomteSection, StringComparison.Ordinal) ? target[TomteSection.Length..] : null;
        return target == TomteSettings || (at is not null && TomtePages.Any(p => p.Section == at));
    }

    /// <summary>
    /// Opens a Windows Settings page or a tool from the table; anything else is refused, so a row read back from the
    /// launch counts can never start more than the table names. Starts a process: a worker thread only.
    /// </summary>
    public static ActionResult Open(string target, bool dryRun = false)
    {
        if (target.StartsWith(SettingsUri, StringComparison.OrdinalIgnoreCase))
        {
            return Apps.OpenSettingsPage(target, dryRun);
        }

        if (Tools.FirstOrDefault(t => ToolTarget(t) == target) is not { Name: { } name } tool)
        {
            return ActionResult.Fail("That is not a place Tomte knows.");
        }

        if (dryRun)
        {
            return ActionResult.DryRun($"open {name}.");
        }

        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, tool.File), tool.Arguments) { UseShellExecute = true };
        return Apps.Launch(start, name);
    }

    private static string ToolTarget((string Name, string File, string Arguments, string Words, string? Icon) tool) =>
        tool.Arguments.Length > 0 ? $"{tool.File} {tool.Arguments}" : tool.File;

    private static bool IsSettingsPage(string target) =>
        target.StartsWith(SettingsUri, StringComparison.OrdinalIgnoreCase) || target.StartsWith(TomteSettings, StringComparison.Ordinal);

    // A match in the words counts its tier alone, not the matcher's bonus for a short text: a short list of words is no
    // better a match. Ties then go to the shorter name, as everywhere in the list.
    private static int Best(SearchItem place, string query)
    {
        int words = SearchMatch.Score($"{place.Name} {place.Words}", query, wordStarts: true);
        return Math.Max(SearchMatch.Score(place.Name, query), words - (words % 100));
    }

    // The query without "settings" and "setting", or null when it has neither or nothing else.
    private static string? WithoutSettings(string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var rest = words.Where(w => !w.Equals("settings", StringComparison.OrdinalIgnoreCase) && !w.Equals("setting", StringComparison.OrdinalIgnoreCase)).ToArray();
        return rest.Length == words.Length || rest.Length == 0 ? null : string.Join(' ', rest);
    }
}
