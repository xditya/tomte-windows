using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices;
using Tomte.Core;
using Tomte.Core.Device;
using Tomte.Core.Interaction;
using Tomte.Core.Text;
using Windows.UI.Composition;
using Windows.Win32;
using Color = Windows.UI.Color;

namespace Tomte.Shell;

/// <summary>
/// The Call tab, laid out by <see cref="CallPanelLayout"/>: the microphone's mute and device, the call's own volume and
/// mute and the speaker, whether the camera is on, how long the call has run, and Switch to call, which brings the call
/// app forward; a device capsule opens the list of devices, where a click makes one the default. Nothing here changes a
/// call app's own settings: the mute and the devices are Windows' own, the volume is the app's line in the mixer. The
/// camera cannot be switched from outside the app, so the row says whether it is on and how to switch it in the app.
/// Reads its state when it shows and after each change made on it; while it shows, the camera watch reports on its own
/// and a one-shot timer moves the clock once a second.
/// </summary>
internal sealed class CallPanel(IslandWindow island, CallMute call, TextRenderer text, Action hide, Action<string>? log) : IPanel, IInteractive
{
    private const int WheelNotch = 120;

    private readonly uint ownPid = (uint)Environment.ProcessId;
    private ContainerVisual? body;
    private CallPanelLayout layout = new(new Vector2((float)PanelSize.Call.W, CallPanelLayout.Height));
    private Parts? parts, bar;
    private TextVisual? clock;
    private Windows.System.DispatcherQueueTimer? tick;
    private CameraWatch? camera;
    private IReadOnlyList<(uint Pid, string Process)> streaming = [];
    private bool cameraKnown;
    private CallSound? sound;
    private CallState? state;
    private IReadOnlyList<Target> targets = [];

    // The device list while it shows: microphones (true) or speakers (false), and the first row in view.
    private bool? listing;
    private IReadOnlyList<AudioDevice> devices = [];
    private int first, wheel;
    private string? failure;

    public PanelId Id => PanelId.Call;

    public IReadOnlyList<Target> Targets => targets;

    public event Action? TargetsChanged;

    public void Shown(ContainerVisual body, Vector2 size)
    {
        this.body = body;
        layout = new CallPanelLayout(size);
        tick = island.Queue.CreateTimer();
        tick.IsRepeating = false;
        tick.Tick += (_, _) => ShowClock();
        call.Changed += Read;
        camera = CameraWatch.Start();
        if (camera is not null)
        {
            camera.Changed += now => island.Queue.TryEnqueue(() =>
            {
                // Reports come on many sensor events; only a change in whose camera is on is drawn.
                if (this.body is not null && call.App is { } app && (!cameraKnown || app.Camera(now, ownPid) != app.Camera(streaming, ownPid)))
                {
                    log?.Invoke($"call: camera streaming to {(now.Count == 0 ? "nobody" : string.Join(", ", now.Select(s => s.Process)))}");
                    (streaming, cameraKnown) = (now, true);
                    Draw();
                }
            });
        }

        Read();
    }

    public void Hidden()
    {
        call.Changed -= Read;
        camera?.Dispose();
        camera = null;
        tick?.Stop();
        tick = null;
        parts?.Dispose();
        bar?.Dispose();
        (parts, bar, clock, body, sound, state) = (null, null, null, null, null, null);
        (listing, devices, first, wheel, failure, streaming, cameraKnown) = (null, [], 0, 0, null, [], false);
        targets = [];
        TargetsChanged?.Invoke();
    }

    /// <summary>Presses come through the targets.</summary>
    public void Press(Vector2 point)
    {
    }

    /// <summary>Over the sound card the wheel turns the volume; over the device list it scrolls.</summary>
    public void Wheel(Vector2 point, int delta)
    {
        wheel += delta;
        int notches = wheel / WheelNotch;
        if (notches == 0 || body is null)
        {
            return;
        }

        wheel -= notches * WheelNotch;
        if (listing is not null)
        {
            int next = layout.Scrolled(first, -notches, devices.Count);
            if (next != first)
            {
                first = next;
                Draw();
            }
        }
        else if (layout.SoundCard.Contains(point.X, point.Y) && sound is not null)
        {
            SetLevel(CallPanelLayout.Stepped(sound.Level, notches, CallPanelLayout.WheelStep));
        }
    }

    public void Activate(string id)
    {
        if (body is null || state is null)
        {
            return;
        }

        switch (id)
        {
            case CallPanelLayout.MicId:
                call.ToggleMute(); // Changed reads again
                break;
            case CallPanelLayout.SoundId when sound is { } s:
                Change(() => s.SetMuted(!s.Muted), "mute");
                break;
            case CallPanelLayout.MicDeviceId or CallPanelLayout.SpeakerDeviceId:
                List(id == CallPanelLayout.MicDeviceId);
                break;
            case CallPanelLayout.SwitchId:
                if (state.App.BringForward())
                {
                    log?.Invoke($"call: brought {state.App.Name} to the front");
                    hide();
                }
                else
                {
                    Fail($"Could not bring {state.App.Name} to the front");
                }

                break;
            case CallPanelLayout.BackId:
                Back();
                break;
            case CallPanelLayout.SettingsId:
                hide();
                Apps.OpenSettingsPage(CallPanelLayout.SettingsPage);
                break;
            default:
                if (id.StartsWith("device:", StringComparison.Ordinal) && int.TryParse(id["device:".Length..], out int i) && i < devices.Count)
                {
                    Pick(devices[i]);
                }

                break;
        }
    }

    /// <summary>A press on the volume bar sets it there, and a drag from there moves it with the pointer; no click follows.</summary>
    public bool TryDrag(string id, Vector2 at)
    {
        if (id != CallPanelLayout.VolumeId || sound is null || !PInvoke.GetCursorPos(out var screen))
        {
            return false;
        }

        SetLevel(layout.LevelAt(at.X));
        if (!PInvoke.DragDetect(island.Hwnd, screen))
        {
            // The click has set it; Esc ending the wait with the button down is no click either.
            return true;
        }

        float Level(Vector2 point) => layout.LevelAt(PanelSpace.ToBody(point, layout.Body.X).X);
        if (PointerDrag.Follow(island, () => body is not null, point => SetLevel(Level(point))) is { } up)
        {
            SetLevel(Level(up));
        }

        return true;
    }

    /// <summary>On the volume bar Left and Right move it 5%, Home and End to the ends.</summary>
    public bool KeyDown(string id, NavKey key, bool repeat)
    {
        if (id != CallPanelLayout.VolumeId || sound is null)
        {
            return false;
        }

        float level = sound.Level;
        float? to = key switch
        {
            NavKey.Left or NavKey.Down => CallPanelLayout.Stepped(level, -1, CallPanelLayout.KeyStep),
            NavKey.Right or NavKey.Up => CallPanelLayout.Stepped(level, 1, CallPanelLayout.KeyStep),
            NavKey.Home => 0,
            NavKey.End => 1,
            _ => null,
        };
        if (to is { } value)
        {
            SetLevel(value);
        }

        return to is not null;
    }

    /// <summary>Esc closes the device list before it retracts the island.</summary>
    public bool Command(NavKey key)
    {
        if (key is not (NavKey.Escape or NavKey.Back) || listing is null)
        {
            return false;
        }

        Back();
        return true;
    }

    /// <summary>The state again from the devices, and drawn.</summary>
    private void Read()
    {
        if (body is null || call.App is not { } app)
        {
            return;
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        string? mic = null, speaker = null;
        try
        {
            mic = Audio.CallDevices(capture: true).FirstOrDefault(d => d.IsDefault)?.Name;
            speaker = Audio.CallDevices(capture: false).FirstOrDefault(d => d.IsDefault)?.Name;
            sound = CallSound.Open(app);
        }
        catch (COMException e)
        {
            log?.Invoke($"call: reading the devices failed, 0x{e.HResult:X8}");
        }

        state = new CallState
        {
            App = app,
            Since = call.Since,
            Ended = !call.Calling,
            MicMuted = call.Muted,
            Mic = mic,
            Level = Safe(() => sound?.Level),
            SoundMuted = Safe(() => sound?.Muted) == true,
            AppSound = sound?.OfApp == true,
            Speaker = speaker,
        };
        log?.Invoke($"call: read in {watch.ElapsedMilliseconds} ms, {(state.AppSound ? "the app's own volume" : "the speaker's volume")}");
        Draw();
    }

    private T? Safe<T>(Func<T?> read)
        where T : struct
    {
        try
        {
            return read();
        }
        catch (COMException)
        {
            // The session or device went: as if there were none.
            sound = null;
            return null;
        }
    }

    private void SetLevel(float level)
    {
        if (sound is null || state is null)
        {
            return;
        }

        try
        {
            sound.SetLevel(level);
            state = state with { Level = level };
            DrawBar();
        }
        catch (COMException e)
        {
            log?.Invoke($"call: volume failed, 0x{e.HResult:X8}");
            Read();
        }
    }

    private void Change(Action change, string what)
    {
        try
        {
            change();
        }
        catch (COMException e)
        {
            log?.Invoke($"call: {what} failed, 0x{e.HResult:X8}");
        }

        Read();
    }

    private void List(bool microphones)
    {
        try
        {
            devices = CallPanelLayout.Order(Audio.CallDevices(microphones));
        }
        catch (COMException e)
        {
            log?.Invoke($"call: listing devices failed, 0x{e.HResult:X8}");
            devices = [];
        }

        int current = devices.ToList().FindIndex(d => d.IsDefault);
        (listing, failure) = (microphones, null);
        // The list opens with the current device in view.
        first = layout.Scrolled(0, current < 0 ? 0 : (current / layout.Columns) - ((layout.Fits / layout.Columns) - 1), devices.Count);
        Draw();
        island.Interaction.FocusFirst(this);
    }

    private void Back()
    {
        (listing, devices, failure) = (null, [], null);
        Read();
        island.Interaction.FocusFirst(this);
    }

    /// <summary>The device the default for every role, as Settings does; a refusal stays on the list and says so.</summary>
    private void Pick(AudioDevice device)
    {
        if (device.IsDefault)
        {
            Back();
            return;
        }

        int hr = DefaultDevice.Set(device.Id);
        log?.Invoke($"call: default {(listing == true ? "microphone" : "speaker")} {(hr == 0 ? "set" : $"refused, 0x{hr:X8}")}");
        if (hr != 0)
        {
            Fail(CallPanelLayout.SwitchFailed(device.Name, hr));
            return;
        }

        Back();
    }

    private void Fail(string why)
    {
        failure = why;
        Draw();
    }

    private void Draw()
    {
        if (body is null || state is null)
        {
            return;
        }

        var (use, other) = state.App.Camera(streaming, ownPid);
        state = state with { Camera = use, CameraApp = use == CameraUse.Other ? CallApp.Of(other)?.Name ?? "another app" : null, Failure = failure };
        parts?.Dispose();
        clock = null;
        var p = parts = new Parts(island.Compositor, text);
        var shapes = p.Shapes(body, new RectangleF(0, 0, layout.Body.X, layout.Body.Y));
        if (listing is { } microphones)
        {
            DrawList(p, shapes, microphones);
            bar?.Dispose();
            bar = null;
            tick?.Stop();
        }
        else
        {
            DrawMain(p, shapes, state);
            DrawBar();
            ShowClock();
        }

        p.Text(shapes, new TextSlot(CallPanelLayout.FootText(state), state.Failure is null ? CallPanelLayout.FootStyle : CallPanelLayout.FailedStyle, layout.Foot));
        var next = listing is null ? layout.Targets(state) : layout.ListTargets(devices, first);
        if (!next.SequenceEqual(targets))
        {
            targets = next;
            TargetsChanged?.Invoke();
        }
    }

    private void DrawMain(Parts p, ShapeVisual shapes, CallState s)
    {
        var card = p.Brush(ShelfColors.Card);
        foreach (var (box, on, muted, mutedGlyph, glyph, overline, device) in new[]
        {
            (layout.MicCard, s.Mic is not null, s.MicMuted, Glyphs.MicOff, Glyphs.Mic, CallPanelLayout.MicOverline, s.Mic),
            (layout.SoundCard, s.Level is not null, s.SoundMuted, Glyphs.SpeakerOff, Glyphs.Speaker, CallPanelLayout.SoundOverline(s), s.Speaker),
        })
        {
            p.Box(shapes, box, Section.Corner, card);
            // The round button: a quiet raised disc, or the warning colour's soft tint with the glyph struck through.
            var disc = CallPanelLayout.DiscIn(box);
            p.Box(shapes, disc, disc.Width / 2, p.Brush(muted ? ShelfColors.WarnFill : ShelfColors.Raised));
            var ink = !on ? ShelfColors.Disabled : muted ? ShelfColors.Warn : ShelfColors.Ink;
            p.Glyph(shapes, muted ? mutedGlyph : glyph, ink, Centred(disc, CallPanelLayout.Glyph));
            p.Text(shapes, new TextSlot(overline, TextStyle.Overline, CallPanelLayout.OverlineIn(box)));
            var capsule = CallPanelLayout.DeviceIn(box);
            p.Box(shapes, capsule, capsule.Height / 2, p.Brush(ShelfColors.Raised));
            p.Text(shapes, new TextSlot(device ?? "No device", CallPanelLayout.DeviceStyle, CallPanelLayout.DeviceText(capsule)));
            p.Glyph(shapes, Glyphs.ChevronDown, ShelfColors.Ink2, CallPanelLayout.ChevronIn(capsule));
        }

        p.Text(shapes, new TextSlot(s.Mic is null ? "No microphone" : CallPanelLayout.MicStatus(s.MicMuted), s.MicMuted ? CallPanelLayout.MutedStyle : CallPanelLayout.StatusStyle, CallPanelLayout.StatusIn(layout.MicCard)));

        // The camera row, the clock (drawn by the timer) and Switch to call.
        var row = layout.Row;
        p.Box(shapes, row, Section.Corner, card);
        p.Glyph(shapes, Glyphs.Camera, s.Camera == CameraUse.Off || !cameraKnown ? ShelfColors.Ink2 : ShelfColors.Ink, layout.CameraGlyph);
        p.Text(shapes, new TextSlot(cameraKnown ? CallPanelLayout.CameraText(s.Camera, s.CameraApp) : "Camera", CallPanelLayout.StatusStyle, layout.CameraLine));
        p.Text(shapes, new TextSlot(CallPanelLayout.CameraHintText(s.App), TextStyle.Small, layout.CameraHint));
        clock = p.Text(shapes, new TextSlot("", CallPanelLayout.ClockStyle, layout.Clock));
        var button = layout.Switch;
        p.Box(shapes, button, Section.ButtonCorner, p.Brush(ShelfColors.Raised));
        p.Text(shapes, new TextSlot(CallPanelLayout.SwitchText, CallPanelLayout.ButtonStyle, button));
    }

    private void DrawList(Parts p, ShapeVisual shapes, bool microphones)
    {
        var back = layout.Back;
        p.Box(shapes, back, back.Width / 2, p.Brush(ShelfColors.Card));
        p.Glyph(shapes, Glyphs.ChevronLeft, ShelfColors.Ink, Centred(back, CallPanelLayout.Glyph));
        p.Text(shapes, new TextSlot(CallPanelLayout.ListTitle(microphones), TextStyle.Title, layout.Title));
        var settings = layout.Settings;
        p.Box(shapes, settings, Section.ButtonCorner, p.Brush(ShelfColors.Card));
        p.Text(shapes, new TextSlot(CallPanelLayout.SettingsText, CallPanelLayout.ButtonStyle, settings));
        if (devices.Count == 0)
        {
            p.Text(shapes, new TextSlot(microphones ? "No microphones" : "No speakers", TextStyle.Secondary, layout.ListRowAt(0)));
        }

        var card = p.Brush(ShelfColors.Card);
        for (int i = first; i < Math.Min(devices.Count, first + layout.Fits); i++)
        {
            var row = layout.ListRowAt(i - first);
            p.Box(shapes, row, Section.TileCorner, devices[i].IsDefault ? p.Brush(ShelfColors.Raised) : card);
            if (devices[i].IsDefault)
            {
                p.Glyph(shapes, Glyphs.Check, ShelfColors.Ink, CallPanelLayout.CheckIn(row));
            }

            p.Text(shapes, new TextSlot(devices[i].Name, TextStyle.Primary, CallPanelLayout.NameIn(row)));
        }
    }

    /// <summary>The volume bar's track, fill and knob and the percentage, drawn alone while the volume moves.</summary>
    private void DrawBar()
    {
        bar?.Dispose();
        bar = null;
        if (body is null || state is null || listing is not null)
        {
            return;
        }

        var p = bar = new Parts(island.Compositor, text);
        var shapes = p.Shapes(body, new RectangleF(0, 0, layout.Body.X, layout.Body.Y));
        var line = layout.Bar;
        bool on = state.Level is not null;
        float level = state.Level ?? 0;
        p.Box(shapes, line, line.Height / 2, p.Brush(ShelfColors.Of(Theme.Track)));
        Color ink = !on ? ShelfColors.Disabled : state.SoundMuted ? ShelfColors.Ink3 : ShelfColors.Ink;
        if (on)
        {
            p.Box(shapes, line with { Width = layout.BarX(level) - line.X }, line.Height / 2, p.Brush(ink));
            float x = layout.BarX(level), y = line.Y + (line.Height / 2);
            p.Box(shapes, RectangleF.FromLTRB(x - CallPanelLayout.Knob, y - CallPanelLayout.Knob, x + CallPanelLayout.Knob, y + CallPanelLayout.Knob), CallPanelLayout.Knob, p.Brush(ink));
        }

        p.Text(shapes, new TextSlot(on ? CallPanelLayout.PercentText(level) : "", CallPanelLayout.PercentStyle, layout.Percent));
    }

    /// <summary>How long the call has run, again as it passes the next whole second while the call is on.</summary>
    private void ShowClock()
    {
        tick?.Stop();
        if (clock is null || state is null || listing is not null)
        {
            return;
        }

        var run = DateTimeOffset.Now - state.Since;
        clock.Show(CallPanelLayout.ClockText(run), CallPanelLayout.ClockStyle, layout.Clock);
        if (call.Calling && tick is not null)
        {
            tick.Interval = TimeSpan.FromMilliseconds(1000 - (run.TotalMilliseconds % 1000) + 10);
            tick.Start();
        }
    }

    private static RectangleF Centred(RectangleF box, float size) => new(box.X + ((box.Width - size) / 2), box.Y + ((box.Height - size) / 2), size, size);
}
