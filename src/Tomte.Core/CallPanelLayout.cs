using System.Drawing;
using System.Globalization;
using System.Numerics;
using Tomte.Core.Device;
using Tomte.Core.Interaction;
using Tomte.Core.Text;

namespace Tomte.Core;

/// <summary>What the Call tab shows, read from the devices when it opens and after each change made on it.</summary>
public sealed record CallState
{
    public required CallApp App { get; init; }

    /// <summary>When the call was first heard: when the app started recording.</summary>
    public DateTimeOffset Since { get; init; }

    /// <summary>The app stopped recording from the default microphone while the tab was open.</summary>
    public bool Ended { get; init; }

    public bool MicMuted { get; init; }

    /// <summary>The default communications microphone's name; null with none.</summary>
    public string? Mic { get; init; }

    /// <summary>The call's volume, 0 to 1 (<see cref="CallSound"/>); null with no output device.</summary>
    public float? Level { get; init; }

    public bool SoundMuted { get; init; }

    /// <summary>The level is the app's own; false for the output device's.</summary>
    public bool AppSound { get; init; }

    /// <summary>The default communications output's name; null with none.</summary>
    public string? Speaker { get; init; }

    public CameraUse Camera { get; init; }

    /// <summary>The app streaming from the camera when it is not the call app.</summary>
    public string? CameraApp { get; init; }

    /// <summary>A change that did not work, said in place of the line at the bottom.</summary>
    public string? Failure { get; init; }
}

/// <summary>
/// Where the Call tab's parts sit, in DIPs from the body's top-left corner (under the tab row). Two section cards side by
/// side, the microphone on the left and the call's sound on the right, each with a round mute button, its overline, a line
/// of state (the sound's is its volume bar and percentage) and the device as a capsule that opens the device list. Under
/// them a row with the camera's state, how long the call has run and Switch to call, and the line about call apps
/// following the default devices. The device list takes the body: a Back button, the list's name and Sound settings in the
/// head row, the devices in rows under it (two columns on a wide body) with a check on the current one.
/// </summary>
public sealed class CallPanelLayout(Vector2 body)
{
    public const float Pad = Section.BodyPad, Top = Section.BodyTop, Bottom = 16, Gap = Section.Gap;
    public const float CardHeight = 106, Disc = 40, Glyph = 18, TextGap = 12, DeviceHeight = 28, DevicePad = 12, Chevron = 14;
    public const float BarHeight = 4, Knob = 6, PercentWidth = 40;
    public const float RowHeight = 52, Button = 32, ButtonPad = 14, ClockWidth = 56, FootGap = 8, FootHeight = 14;
    public const float ListRow = 30, ListGap = 2, Check = 16, TwoColumnsFrom = 560;

    /// <summary>The body's height: the cards, the camera row and the line under it.</summary>
    public const float Height = Top + CardHeight + Gap + RowHeight + FootGap + FootHeight + Bottom;

    /// <summary>One arrow key on the volume bar, and one notch of the wheel over the sound card.</summary>
    public const float KeyStep = 0.05f, WheelStep = 0.02f;

    public const string MicId = "mic", MicDeviceId = "mic-device", SoundId = "sound", VolumeId = "volume", SpeakerDeviceId = "speaker-device", SwitchId = "switch";
    public const string BackId = "back", SettingsId = "sound-settings";
    public const string SwitchText = "Switch to call", SettingsText = "Sound settings", SettingsPage = "ms-settings:sound";
    public const string MicOverline = "MICROPHONE", Follow = "Call apps follow these devices only when set to Default in their own settings";

    public static TextStyle StatusStyle => TextStyle.Primary;
    public static TextStyle MutedStyle => TextStyle.Primary with { Color = Theme.Warn };
    public static TextStyle DeviceStyle => TextStyle.Secondary with { Color = Theme.Ink };
    public static TextStyle PercentStyle => new(12, Color: Theme.Ink2, Align: TextAlign.Trailing, Tabular: true);
    public static TextStyle ClockStyle => new(12, Color: Theme.Ink3, Align: TextAlign.Trailing, Tabular: true, Family: TextStyle.Mono);
    public static TextStyle ButtonStyle => TextStyle.Primary with { Align = TextAlign.Center };
    public static TextStyle FootStyle => TextStyle.Small;
    public static TextStyle FailedStyle => TextStyle.Small with { Color = Theme.Warn };

    public Vector2 Body { get; } = body;

    private float Half => (Body.X - (2 * Pad) - Gap) / 2;

    public RectangleF MicCard => new(Pad, Top, Half, CardHeight);

    public RectangleF SoundCard => new(Pad + Half + Gap, Top, Half, CardHeight);

    /// <summary>The camera row across the body under the cards.</summary>
    public RectangleF Row => new(Pad, Top + CardHeight + Gap, Body.X - (2 * Pad), RowHeight);

    public RectangleF Foot => new(Pad, Row.Bottom + FootGap, Body.X - (2 * Pad), FootHeight);

    /// <summary>A card's round mute button, at its top-left padding.</summary>
    public static RectangleF DiscIn(RectangleF card) => new(card.X + Section.Pad, card.Y + Section.Pad, Disc, Disc);

    public static RectangleF OverlineIn(RectangleF card) => Beside(card, card.Y + Section.Pad + 2, 14);

    /// <summary>The line under the overline, beside the button: the microphone's state, or the volume bar and its percentage.</summary>
    public static RectangleF StatusIn(RectangleF card) => Beside(card, card.Y + Section.Pad + 20, 20);

    /// <summary>The device capsule across the card under the button.</summary>
    public static RectangleF DeviceIn(RectangleF card) => new(card.X + Section.Pad, DiscIn(card).Bottom + 10, card.Width - (2 * Section.Pad), DeviceHeight);

    public static RectangleF DeviceText(RectangleF device) => RectangleF.FromLTRB(device.X + DevicePad, device.Y, device.Right - DevicePad - Chevron - 6, device.Bottom);

    public static RectangleF ChevronIn(RectangleF device) => new(device.Right - DevicePad - Chevron, device.Y + ((device.Height - Chevron) / 2), Chevron, Chevron);

    /// <summary>The volume bar, which takes the sound card's status line less the percentage.</summary>
    public RectangleF Bar
    {
        get
        {
            var line = StatusIn(SoundCard);
            float mid = line.Y + (line.Height / 2);
            return RectangleF.FromLTRB(line.X + Knob, mid - (BarHeight / 2), line.Right - PercentWidth - 8, mid + (BarHeight / 2));
        }
    }

    public RectangleF Percent => RectangleF.FromLTRB(StatusIn(SoundCard).Right - PercentWidth, StatusIn(SoundCard).Y, StatusIn(SoundCard).Right, StatusIn(SoundCard).Bottom);

    /// <summary>What a press on the bar is tested against: 24 DIPs tall, as far past each end as the knob reaches.</summary>
    public RectangleF Volume => new(Bar.X - Knob, Bar.Y + (Bar.Height / 2) - 12, Bar.Width + (2 * Knob), 24);

    public RectangleF CameraGlyph => new(Row.X + Section.Pad, Row.Y + ((Row.Height - Glyph) / 2), Glyph, Glyph);

    public RectangleF Switch
    {
        get
        {
            float w = MathF.Ceiling(TextSlot.Measure(SwitchText, ButtonStyle, Row.Width)) + (2 * ButtonPad);
            return new(Row.Right - Section.Pad + 4 - w, Row.Y + ((Row.Height - Button) / 2), w, Button);
        }
    }

    public RectangleF Clock => new(Switch.X - TextGap - ClockWidth, Row.Y, ClockWidth, Row.Height);

    /// <summary>The camera's state, and under it how to turn it on or off.</summary>
    public RectangleF CameraLine => RectangleF.FromLTRB(CameraGlyph.Right + TextGap, Row.Y + 8, Clock.X - TextGap, Row.Y + 26);

    public RectangleF CameraHint => RectangleF.FromLTRB(CameraLine.X, Row.Y + 27, CameraLine.Right, Row.Y + 43);

    /// <summary>The main view's targets in Tab order. A part with nothing behind it (no microphone, no output) is disabled.</summary>
    public IReadOnlyList<Target> Targets(CallState state)
    {
        bool mic = state.Mic is not null, sound = state.Level is not null;
        var micDisc = DiscIn(MicCard);
        var soundDisc = DiscIn(SoundCard);
        return
        [
            new(MicId, micDisc, Disc / 2, TargetKind.Toggle, mic, CompactLayout.MicTip(state.MicMuted), Checked: state.MicMuted),
            new(MicDeviceId, DeviceIn(MicCard), DeviceHeight / 2, TargetKind.Button, Tip: "Choose a microphone"),
            new(SoundId, soundDisc, Disc / 2, TargetKind.Toggle, sound, SoundTip(state), Checked: state.SoundMuted),
            new(VolumeId, Volume, Volume.Height / 2, TargetKind.Slider, sound, VolumeTip(state), Draggable: true),
            new(SpeakerDeviceId, DeviceIn(SoundCard), DeviceHeight / 2, TargetKind.Button, Tip: "Choose a speaker"),
            new(SwitchId, Switch, Button / 2, TargetKind.Button, Tip: $"Bring {state.App.Name} to the front"),
        ];
    }

    /// <summary>The volume a point on the bar stands for.</summary>
    public float LevelAt(float x) => Math.Clamp((x - Bar.X) / Bar.Width, 0, 1);

    public float BarX(float level) => Bar.X + (Bar.Width * Math.Clamp(level, 0, 1));

    /// <summary><paramref name="steps"/> steps of <paramref name="step"/> on from <paramref name="level"/>, held to 0 and 1.</summary>
    public static float Stepped(float level, int steps, float step) => Math.Clamp(MathF.Round((level + (steps * step)) * 100) / 100, 0, 1);

    public static string MicStatus(bool muted) => muted ? "Muted for every app" : "On";

    /// <summary>The sound card's overline: the call app's volume, or the speaker's when the app has none of its own.</summary>
    public static string SoundOverline(CallState state) =>
        (state.AppSound ? $"{state.App.Name} volume" : "Speaker volume").ToUpper(CultureInfo.CurrentCulture);

    public static string PercentText(float level) => $"{MathF.Round(level * 100).ToString(CultureInfo.InvariantCulture)}%";

    public static string SoundTip(CallState state) =>
        $"{(state.SoundMuted ? "Unmute" : "Mute")} {(state.AppSound ? state.App.Name : "the speaker")}";

    public static string VolumeTip(CallState state) => state.AppSound ? $"{state.App.Name} only; other apps keep their volume" : "The speaker's volume";

    public static string CameraText(CameraUse use, string? other) => use switch
    {
        CameraUse.Call => "Camera on",
        CameraUse.Other => $"Camera in use by {other}",
        _ => "Camera off",
    };

    /// <summary>How to turn the camera on or off: in the call app, with its own keys where it has them.</summary>
    public static string CameraHintText(CallApp app) =>
        app.CameraKeys is { } keys ? $"Switch to the call, then {keys} turns it on or off" : $"Turn it on or off in {app.Name}";

    /// <summary>How long the call has run: 4:05, or 1:02:03 past an hour.</summary>
    public static string ClockText(TimeSpan run)
    {
        run = run < TimeSpan.Zero ? TimeSpan.Zero : run;
        return run.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)run.TotalHours}:{run.Minutes:00}:{run.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{run.Minutes}:{run.Seconds:00}");
    }

    /// <summary>The line at the bottom: what went wrong, that the call ended, or how call apps pick their devices.</summary>
    public static string FootText(CallState state) =>
        state.Failure ?? (state.Ended ? $"{state.App.Name} stopped using the default microphone" : Follow);

    /// <summary>A device switch Windows refused.</summary>
    public static string SwitchFailed(string device, int hresult) =>
        string.Create(CultureInfo.InvariantCulture, $"Windows did not switch to {device} (0x{hresult:X8}). Sound settings can");

    /// <summary>The device list's order: by name, so it reads the same each time, the current one wherever it falls.</summary>
    public static IReadOnlyList<AudioDevice> Order(IEnumerable<AudioDevice> devices) =>
        [.. devices.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(d => d.Id, StringComparer.Ordinal)];

    public static string DeviceId(int index) => $"device:{index}";

    public static string ListTitle(bool microphones) => microphones ? "Choose a microphone" : "Choose a speaker";

    public RectangleF Back => new(Pad, Top, Button, Button);

    public RectangleF Settings
    {
        get
        {
            float w = MathF.Ceiling(TextSlot.Measure(SettingsText, ButtonStyle, Body.X)) + (2 * ButtonPad);
            return new(Body.X - Pad - w, Top, w, Button);
        }
    }

    public RectangleF Title => RectangleF.FromLTRB(Back.Right + TextGap, Top, Settings.X - TextGap, Top + Button);

    public int Columns => Body.X >= TwoColumnsFrom ? 2 : 1;

    private float ListTop => Top + Button + Gap;

    /// <summary>How many device rows the list shows at once.</summary>
    public int Fits => Columns * Math.Max(1, (int)((Foot.Y - FootGap - ListTop + ListGap) / (ListRow + ListGap)));

    /// <summary>The <paramref name="shown"/>th row on the list (0 the first in view), across then down.</summary>
    public RectangleF ListRowAt(int shown)
    {
        float width = (Body.X - (2 * Pad) - ((Columns - 1) * Gap)) / Columns;
        int column = shown % Columns, line = shown / Columns;
        return new(Pad + (column * (width + Gap)), ListTop + (line * (ListRow + ListGap)), width, ListRow);
    }

    public static RectangleF CheckIn(RectangleF row) => new(row.X + 10, row.Y + ((row.Height - Check) / 2), Check, Check);

    public static RectangleF NameIn(RectangleF row) => RectangleF.FromLTRB(row.X + 10 + Check + 8, row.Y, row.Right - 10, row.Bottom);

    /// <summary>The first row in view after scrolling <paramref name="steps"/> lines from <paramref name="first"/>, held so the list never scrolls past its end.</summary>
    public int Scrolled(int first, int steps, int count)
    {
        int last = Math.Max(0, (int)Math.Ceiling((count - Fits) / (double)Columns) * Columns);
        return Math.Clamp(first + (steps * Columns), 0, last);
    }

    /// <summary>The device list's targets: Back, Sound settings, then the rows in view, one arrow-key group.</summary>
    public IReadOnlyList<Target> ListTargets(IReadOnlyList<AudioDevice> devices, int first)
    {
        var list = new List<Target>
        {
            new(BackId, Back, Button / 2, TargetKind.Button, Tip: "Back", Key: "Esc"),
            new(SettingsId, Settings, Button / 2, TargetKind.Link),
        };
        for (int i = first; i < Math.Min(devices.Count, first + Fits); i++)
        {
            list.Add(new(DeviceId(i), ListRowAt(i - first), Section.TileCorner, TargetKind.Row, Group: 1, Checked: devices[i].IsDefault));
        }

        return list;
    }

    private static RectangleF Beside(RectangleF card, float y, float height)
    {
        float x = DiscIn(card).Right + TextGap;
        return new(x, y, card.Right - Section.Pad - x, height);
    }
}
