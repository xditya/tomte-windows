using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using Tomte.Core;
using Tomte.Core.Interaction;
using Tomte.Core.Media;
using Tomte.Core.Rambler;
using Tomte.Core.Shortcuts;
using Tomte.Core.Text;
using Tomte.Core.Triggers;
using Tomte.Shell;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.Win32;
using Windows.Win32.Graphics.Direct2D;
using Windows.Win32.Graphics.Direct2D.Common;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.System.ProcessStatus;

namespace TextCaptures;

/// <summary>
/// Offscreen captures of every pill and panel that draws text, at 100%, 125% and 150%, with no window. The text
/// is the island's own: its layout, its pixel snapping and its Direct2D call on the software device, each run
/// drawn into a transparent surface of its own and laid over the pill as the compositor does. Around it, the
/// pill's real outline, gradient and hairline and the mascot's real paths; art, buttons, bars and ring are drawn
/// from the numbers the island's composition shapes use. The drop shadow is left out. Also prints what the text
/// costs in time and memory.
/// Usage: TextCaptures &lt;output folder&gt;
/// </summary>
internal static unsafe partial class Program
{
    private static readonly double[] Scales = [1, 1.25, 1.5];
    private static readonly List<double> LayoutMs = [], DrawMs = [];
    private static int OffGrid;

    // STA, as the island's thread is: the text services' input and drop support need it.
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--devices", var how])
        {
            return Devices(how == "shared");
        }

        if (args is ["--typing", var where, ..])
        {
            return Typing.Run(where, measure: !args.Contains("--no-measure"));
        }

        string dir = args.Length > 0 ? args[0] : "captures";
        Directory.CreateDirectory(dir);
        var report = new List<string> { $"machine {Environment.MachineName}, {Environment.OSVersion}" };
        report.Add(Memory("start"));

        var watch = Stopwatch.StartNew();
        using (var first = TextLayout.Of(new TextRun("Nightfall", MediaPanelLayout.TitleStyle, MediaPanelLayout.Title.Width)))
        {
            report.Add($"first layout (DirectWrite factory, font and format): {watch.Elapsed.TotalMilliseconds:F1} ms");
        }

        report.Add(Memory("after the first layout"));
        watch.Restart();
        var d2d = TextDraw.CreateDevice(out ID3D11Device* d3d);
        report.Add($"first device (WARP Direct3D 11 and Direct2D): {watch.Elapsed.TotalMilliseconds:F1} ms");
        ID2D1DeviceContext* dc;
        d2d->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS.D2D1_DEVICE_CONTEXT_OPTIONS_NONE, &dc);
        report.Add(Memory("after the device"));
        watch.Restart();
        var again = TextDraw.CreateDevice(out ID3D11Device* d3dAgain);
        report.Add($"a second device in the same process: {watch.Elapsed.TotalMilliseconds:F1} ms");
        again->Release();
        d3dAgain->Release();

        foreach (double scale in Scales)
        {
            foreach (var scene in Scenes())
            {
                var canvas = new Canvas(dc, scene.Geometry, scale);
                scene.Draw(canvas);
                string file = Path.Combine(dir, $"{scale * 100:0}-{scene.Name}.png");
                canvas.Save(file);
                canvas.Dispose();
            }
        }

        report.Add(Memory($"after {Scales.Length * Scenes().Count} captures"));
        report.Add($"per run: layout {Stats(LayoutMs)}, draw {Stats(DrawMs)} ({LayoutMs.Count} runs)");
        report.Add($"runs whose surface does not start on a whole pixel: {OffGrid} of {DrawMs.Count}");
        report.Add($"a two-line run cut with an ellipsis draws nothing below its second line: {TwoLinesOnly(dc)}");

        dc->Release();
        d2d->Release();
        d3d->Release();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        report.Add(Memory("after the device and the layouts are released"));

        File.WriteAllLines(Path.Combine(dir, "text-costs.txt"), report);
        foreach (var line in report)
        {
            Console.WriteLine(line);
        }

        return 0;
    }

    /// <summary>
    /// The Media panel's devices with art showing, in a fresh process: the text's Direct2D device on a software
    /// Direct3D device, the four runs drawn, and the art's 132 px texture either on a second software device (as the
    /// island has it) or on the text's one. Prints the private working set before and after.
    /// </summary>
    private static int Devices(bool shared)
    {
        // The text stack's first layout comes first in both, as in the island, so only the devices differ.
        TextLayout.Of(new TextRun("Nightfall", MediaPanelLayout.TitleStyle, MediaPanelLayout.Title.Width)).Dispose();
        GC.Collect();
        string before = Memory("before the devices");
        var d2d = TextDraw.CreateDevice(out ID3D11Device* d3d);
        ID2D1DeviceContext* dc;
        d2d->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS.D2D1_DEVICE_CONTEXT_OPTIONS_NONE, &dc);
        foreach (var (text, style, slot) in new[]
        {
            ("Nightfall", MediaPanelLayout.TitleStyle, MediaPanelLayout.Title),
            ("Lo-fi radio · lofi beats to study", MediaPanelLayout.ArtistStyle, MediaPanelLayout.Artist),
            ("00:48", MediaPanelLayout.ElapsedStyle, MediaPanelLayout.Elapsed),
            ("03:15", MediaPanelLayout.TotalStyle, MediaPanelLayout.Total),
        })
        {
            using var block = new TextBlock();
            block.Set(new TextRun(text, style, slot.Width), 1.5);
            var (w, h) = block.Pixels;
            Canvas.Render(dc, w, h, 1.5, block.Layout!, Vector2.Zero);
        }

        ID3D11Device* art = d3d;
        if (shared)
        {
            d3d->AddRef();
        }
        else
        {
            ID3D11Device* own;
            PInvoke.D3D11CreateDevice(null, Windows.Win32.Graphics.Direct3D.D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_WARP, default, D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT, null, 0, PInvoke.D3D11_SDK_VERSION, &own, null, null).ThrowOnFailure();
            art = own;
        }

        var pixels = new byte[132 * 132 * 4];
        Array.Fill(pixels, (byte)0x80);
        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = 132,
            Height = 132,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
        };
        ID3D11Texture2D* texture;
        fixed (byte* bits = pixels)
        {
            var data = new D3D11_SUBRESOURCE_DATA { pSysMem = bits, SysMemPitch = 132 * 4 };
            art->CreateTexture2D(&desc, &data, &texture);
        }

        GC.Collect();
        Console.WriteLine(before);
        Console.WriteLine(Memory(shared ? "with art and text on one device" : "with art and text on two devices"));
        texture->Release();
        art->Release();
        dc->Release();
        d2d->Release();
        d3d->Release();
        return 0;
    }

    private static string Stats(List<double> ms) => ms.Count == 0 ? "none" : $"avg {ms.Average():F3} ms, max {ms.Max():F3} ms";

    private static string Memory(string moment)
    {
        PROCESS_MEMORY_COUNTERS_EX2 counters = default;
        counters.cb = (uint)sizeof(PROCESS_MEMORY_COUNTERS_EX2);
        PInvoke.K32GetProcessMemoryInfo(PInvoke.GetCurrentProcess(), (PROCESS_MEMORY_COUNTERS*)&counters, counters.cb);
        return $"memory {moment}: private working set {counters.PrivateWorkingSetSize / 1048576.0:F1} MB, working set {counters.WorkingSetSize / 1048576.0:F1} MB";
    }

    /// <summary>Lays a two-line cut out in a box six lines tall and looks for ink under the second line.</summary>
    private static bool TwoLinesOnly(ID2D1DeviceContext* dc)
    {
        using var layout = TextLayout.Of(new TextRun("A very long title that will not fit in the slot at all, not even close, and goes on for a while", MediaPanelLayout.TitleStyle, 120, 2));
        const double scale = 1.5;
        int width = PixelGrid.Cover(120, scale), height = PixelGrid.Cover(layout.Height * 3, scale), cut = PixelGrid.Cover(layout.Height, scale) + 1;
        var pixels = Canvas.Render(dc, width, height, scale, layout, Vector2.Zero);
        for (int y = cut; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (pixels[(y * width + x) * 4 + 3] != 0)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private sealed record Scene(string Name, IslandGeometry Geometry, Action<Canvas> Draw);

    private static List<Scene> Scenes()
    {
        var media = IslandGeometry.Panel(PanelSize.Media.W, PanelSize.Media.H);
        var playing = new Track("Nightfall", "Lo-fi radio · lofi beats to study", 48, 195, true);
        return
        [
            new("media-playing", media, c => MediaPanel(c, playing, null)),
            new("media-paused-long-title", media, c => MediaPanel(c, playing with { Title = "A Very Long Song Title That Goes On (Extended Live Version, Remastered 2026)", Playing = false }, null)),
            new("media-app-name-hour", media, c => MediaPanel(c, new Track("Episode 212: Small tools", "", 3795, 7800, true, App: "Spotify"), null)),
            new("media-no-timeline", media, c => MediaPanel(c, new Track("Radio Paradise", "Live stream", 0, 0, true, Timeline: false), null)),
            new("media-command-failed", media, c => MediaPanel(c, playing, "Spotify did not accept next.")),
            new("media-nothing-playing", media, c => MediaPanel(c, null, null)),
            new("compact-now-playing", IslandGeometry.Compact, c => NowPlayingRow(c, "Nightfall")),
            new("compact-now-playing-long", IslandGeometry.Compact, c => NowPlayingRow(c, "A Very Long Song Title That Goes On")),
            new("compact-timer-running", IslandGeometry.Compact, c => CompactLine(c, CompactRules.Countdown(TimeSpan.FromSeconds(1499.4)), ring: 0.0004f, tabular: true)),
            new("compact-timer-done", IslandGeometry.Compact, c => CompactLine(c, CompactRules.TimerDone(TimeSpan.FromMinutes(25)), ring: 1)),
            new("compact-device-event", IslandGeometry.Compact, c => CompactLine(c, "Volume 40%.", ring: null)),
            new("compact-digest", IslandGeometry.Compact, c => CompactLine(c, CompactRules.EventLine([new IslandEvent(CompactRules.TimerDone(TimeSpan.FromMinutes(25)), default), new IslandEvent("Opened Spotify.", default)]), ring: null)),
            new("compact-first-run-hint", IslandGeometry.Compact, c => CompactLine(c, CompactRules.Hint, ring: null)),
            new("compact-glance-job", IslandGeometry.Compact, c => CompactLine(c, Glance(new GlanceFacts { Job = ("Weekly report", GlanceNow.AddMinutes(19)) }), ring: null)),
            new("compact-glance-job-long", IslandGeometry.Compact, c => CompactLine(c, Glance(new GlanceFacts { Job = ("Send the weekly status report to the team", GlanceNow.AddMinutes(19)) }), ring: null)),
            new("compact-glance-waiting", IslandGeometry.Compact, c => CompactLine(c, Glance(new GlanceFacts { Waiting = [new IslandEvent("Tomte found a damaged database and started a new one.", default)] }), ring: null)),
            new("compact-glance-waiting-two", IslandGeometry.Compact, c => CompactLine(c, Glance(new GlanceFacts { Waiting = [new IslandEvent("Summary ready", default), new IslandEvent("Timer done", default)] }), ring: null)),
            new("compact-glance-battery", IslandGeometry.Compact, c => CompactLine(c, Glance(new GlanceFacts { Battery = 15, ShelfCount = 3 }), ring: null)),
            new("compact-glance-shelf", IslandGeometry.Compact, c => CompactLine(c, Glance(new GlanceFacts { ShelfCount = 3 }), ring: null)),
            new("compact-glance-date", IslandGeometry.Compact, c => CompactLine(c, Glance(new GlanceFacts()), ring: null)),
            // The push pill while the pointer rests on it, about to open: grown on its springs, the line where it was.
            new("compact-glance-swollen", IslandGeometry.Compact.Swollen(), c => CompactLine(c, Glance(new GlanceFacts { ShelfCount = 3 }), ring: null)),
            new("compact-peek", IslandGeometry.Peek, c =>
            {
                c.Pill();
                c.Mascot();
            }),
            new("strip-ask-widest", IslandGeometry.Panel(PanelSize.Shelf.W, 64), c => Strip(c, PanelId.Shelf, PanelSize.Shelf)),
            new("strip-ask-narrowest", IslandGeometry.Panel(PanelSize.Media.W, 64), c => Strip(c, PanelId.Media, PanelSize.Media)),
            // The interaction host's looks over the strip: hover and pressed on a tab, the focus ring on the current one,
            // the Ask pill's tooltip and a "Copied" acknowledgement.
            new("strip-hover", StripLooks, c => StripLook(c, layer: StateLook.Hover)),
            new("strip-pressed", StripLooks, c => StripLook(c, layer: StateLook.Pressed)),
            new("strip-focus", StripLooks, c => StripLook(c, ring: true)),
            new("strip-tip", StripLooks, c => StripLook(c, tip: "Ask Tomte anything")),
            new("strip-acknowledged", StripLooks, c => StripLook(c, tip: "Copied", tick: true)),
            new("shelf", Panel(PanelSize.Shelf), c => Shelf(c, ShelfItems)),
            new("shelf-empty", Panel(PanelSize.Shelf), c => Shelf(c, [])),
            new("shelf-drop", IslandGeometry.ShelfDrop, ShelfDrop),
            new("shortcuts", Panel(PanelSize.Shortcuts), Shortcuts),
            new("scratchpad", Panel(PanelSize.Scratchpad), c => Scratchpad(c, "Call Mom\nSubscribe to the newsletter about small tools, the one with the long name that wraps over two lines\nGym 6:00 PM")),
            new("scratchpad-markdown", Panel(PanelSize.Scratchpad), c => Scratchpad(c, MarkdownNote)),
            new("palette-prompt", IslandGeometry.Panel(PanelSize.PaletteWidth, PanelSize.PaletteMinHeight), c => Palette(c, "What does this error mean?", Chips(false), "Nothing sent", [], 0, 0.03m)),
            new("palette-streaming", IslandGeometry.Panel(PanelSize.PaletteWidth, StreamingHeight()), c => Palette(c, "Summarize the selected text\nin two lines", Chips(true), "Sending to ollama · llama3.2:3b", Streaming, 1, 0.03m)),
            new("palette-ai-off", IslandGeometry.Panel(PanelSize.PaletteWidth, PanelSize.PaletteMinHeight), c => Palette(c, null, [], "", [], 0, 0)),
            new("palette-long-answer-scrolled", IslandGeometry.Panel(PanelSize.PaletteWidth, PanelSize.PaletteMaxHeight), c => Palette(c, "How do I fix a stuck Windows update?", Chips(false), "Sent to ollama · llama3.2:3b", Long, 2, 0.04m, scrolled: 0.5f)),
            new("palette-confirm", IslandGeometry.Panel(PanelSize.PaletteWidth, AskHeight(Asking, voice: false)), c => Palette(c, "Open the Tomte repository", Chips(false), "Sending to ollama · llama3.2:3b", Asked, 0, 0.03m, confirm: Asking)),
            new("palette-voice-confirm", IslandGeometry.Panel(PanelSize.PaletteWidth, AskHeight(PowerAsking, voice: true)), c => Palette(c, "Turn the computer off", [], "Sending to ollama · llama3.2:3b", [], 0, 0.01m, confirm: PowerAsking, voice: true)),
            new("clipboard-history", Panel(PanelSize.Clipboard), c => Clipboard(c, on: true, Rows)),
            new("clipboard-off", Panel(PanelSize.Clipboard), c => Clipboard(c, on: false, [])),
            new("clipboard-empty", Panel(PanelSize.Clipboard), c => Clipboard(c, on: true, [])),
            .. DropZoneScenes(),
        ];
    }

    private sealed record Track(string Title, string Artist, double Position, double Length, bool Playing, string App = "Spotify", bool Timeline = true);

    // The Media panel's view: the same slots, styles and fallbacks as the island's.
    private static void MediaPanel(Canvas c, Track? track, string? failure)
    {
        var size = PanelSize.Media;
        var body = new Vector2((float)(-size.W / 2), PanelSpace.TabStripHeight);
        c.Pill();
        c.Mascot();
        Tabs(c, PanelId.Media, PanelSize.Media);
        var art = Offset(MediaPanelLayout.Art, body);
        if (track is null)
        {
            c.RoundRect(art, MediaPanelLayout.ArtCorner, 0x1CFFFFFF);
        }
        else
        {
            c.Gradient(art, MediaPanelLayout.ArtCorner, 0xFF6A4A86, 0xFFC07A8E);
        }

        if (track is { Timeline: true })
        {
            var bar = Offset(MediaPanelLayout.Bar, body);
            c.RoundRect(bar, bar.Height / 2, 0x29FFFFFF);
            c.RoundRect(bar with { Width = bar.Width * (float)(track.Position / track.Length) }, bar.Height / 2, 0xFFFFFFFF);
        }

        uint lit = track is null ? 0x61FFFFFF : 0xFFFFFFFF;
        c.Glyph(Offset(MediaPanelLayout.Previous, body), lit, [new(6, 4, 2, 16)], [[new(20, 4), new(9, 12), new(20, 20)]]);
        var button = Offset(MediaPanelLayout.PlayPause, body);
        c.Circle(new Vector2(button.X + button.Width / 2, button.Y + button.Height / 2), button.Width / 2, lit);
        if (track is { Playing: true })
        {
            c.Glyph(button, 0xFF000000, [new(6, 4, 4, 16), new(14, 4, 4, 16)], []);
        }
        else
        {
            c.Glyph(button, 0xFF000000, [], [[new(7, 4), new(21, 12), new(7, 20)]]);
        }

        c.Glyph(Offset(MediaPanelLayout.Next, body), lit, [new(16, 4, 2, 16)], [[new(4, 4), new(15, 12), new(4, 20)]]);

        c.Text(track is null ? NowPlaying.NothingPlaying : track.Title, MediaPanelLayout.TitleStyle, MediaPanelLayout.Title, body);
        c.Text(failure ?? (track is null ? "" : track.Artist.Length > 0 ? track.Artist : track.App), failure is null ? MediaPanelLayout.ArtistStyle : MediaPanelLayout.FailedStyle, MediaPanelLayout.Artist, body);
        if (track is { Timeline: true })
        {
            c.Text(NowPlaying.Clock(TimeSpan.FromSeconds(track.Position)), MediaPanelLayout.ElapsedStyle, MediaPanelLayout.Elapsed, body);
            c.Text(NowPlaying.Clock(TimeSpan.FromSeconds(track.Length)), MediaPanelLayout.TotalStyle, MediaPanelLayout.Total, body);
        }
    }

    // The Compact now-playing row: art, the title in its slot, four bars caught mid-breath.
    private static void NowPlayingRow(Canvas c, string title)
    {
        c.Pill();
        c.Mascot();
        c.Gradient(CompactLayout.Art, CompactLayout.ArtCorner, 0xFF6A4A86, 0xFFC07A8E);
        c.Text(title, CompactLayout.TitleStyle, CompactLayout.Title, Vector2.Zero);
        for (int i = 0; i < CompactLayout.BarReach.Length; i++)
        {
            float h = Waveform.MinHeight + Waveform.Swing * CompactLayout.BarReach[i] * 0.7f;
            var bar = new RectangleF(CompactLayout.BarsLeft + i * (Waveform.BarWidth + Waveform.Gap), CompactLayout.BarsTop + (CompactLayout.BarsHeight - h) / 2, Waveform.BarWidth, h);
            c.RoundRect(bar, Waveform.BarWidth / 2, 0xFFFFFFFF);
        }
    }

    // 18:41 on the day the captures were made, in this PC's culture, as the push pill lays it out.
    private static readonly DateTimeOffset GlanceNow = new(DateTime.Today.AddHours(18).AddMinutes(41));

    private static string Glance(GlanceFacts facts) =>
        CompactRules.Glance(GlanceNow, facts, CompactLayout.Line(true, ring: false).Width, line => TextSlot.Measure(line, CompactLayout.LineStyle, 4096));

    // The Compact line in its slot, after the ring when the ring shows; the tomte is on.
    private static void CompactLine(Canvas c, string line, float? ring, bool tabular = false)
    {
        var pill = IslandGeometry.Compact;
        c.Pill();
        c.Mascot();
        if (ring is { } fill)
        {
            float left = (float)(-pill.Width / 2) + CompactLayout.RingLeft(true);
            c.Ring(new Vector2(left + CompactLayout.RingSize / 2, (float)pill.Depth / 2), (CompactLayout.RingSize - CompactLayout.RingStroke) / 2, CompactLayout.RingStroke, fill);
        }

        c.Text(line, tabular ? CompactLayout.ClockStyle : CompactLayout.LineStyle, CompactLayout.Line(true, ring is not null), Vector2.Zero);
    }

    private static IslandGeometry Panel((double W, double H) size) => IslandGeometry.Panel(size.W, size.H);

    // The strip row as the panel host draws it: every tab on main, the current one white on a card, and the Ask pill.
    private static void Tabs(Canvas c, PanelId current, (double W, double H) size)
    {
        var strip = new Vector2((float)(-size.W / 2), 0);
        var labels = TabStrip.Labels(Enum.GetValues<PanelId>(), current, (float)size.W, mascot: true);
        foreach (var line in labels)
        {
            if (line.Style == TabStrip.Current)
            {
                c.RoundRect(Offset(line.Slot, strip), TabStrip.Corner, 0x1CFFFFFF);
            }

            c.Text(line, strip);
        }

        var (card, glyph, word) = TabStrip.AskPill((float)size.W);
        c.RoundRect(Offset(card, strip), TabStrip.Corner, TabStrip.AskCard);
        c.Sparkle(Offset(glyph, strip), TabStrip.AskColor);
        c.Text(word, strip);
    }

    // The strip alone over a short pill, at a panel's width.
    private static void Strip(Canvas c, PanelId current, (double W, double H) size)
    {
        c.Pill();
        c.Mascot();
        Tabs(c, current, size);
    }

    private static readonly IslandGeometry StripLooks = IslandGeometry.Panel(PanelSize.Shelf.W, 110);

    // The strip on the Shelf tab with what the interaction host draws over it, from the numbers it draws with: a state
    // layer on the Shortcuts tab, the focus ring on the current one, or a card under the Ask pill (a tooltip) or under
    // the Shortcuts tab (an acknowledgement with its tick).
    private static void StripLook(Canvas c, uint? layer = null, bool ring = false, string? tip = null, bool tick = false)
    {
        var size = PanelSize.Shelf;
        Strip(c, PanelId.Shelf, size);
        var strip = new Vector2((float)(-size.W / 2), 0);
        var labels = TabStrip.Labels(Enum.GetValues<PanelId>(), PanelId.Shelf, (float)size.W, mascot: true);
        var shortcuts = Offset(labels[1].Slot, strip);
        if (layer is { } fill)
        {
            c.RoundRect(shortcuts, TabStrip.Corner, fill);
        }

        if (ring)
        {
            var (line, corner) = StateLook.FocusRing(Offset(labels[0].Slot, strip), TabStrip.Corner);
            c.Stroke(line, corner, StateLook.Accent, StateLook.FocusWidth);
        }

        if (tip is not null)
        {
            var target = tick ? shortcuts : Offset(TabStrip.AskPill((float)size.W).Card, strip);
            var g = StripLooks;
            var (card, mark, label) = StateLook.Tip(tip, tick, target, new RectangleF((float)(-g.Width / 2), 0, (float)g.Width, (float)g.Depth));
            c.RoundRect(card, StateLook.TipCorner, StateLook.TipFill);
            c.StrokeRect(card, StateLook.TipCorner, StateLook.TipLine);
            if (tick)
            {
                c.Shape(StateLook.Tick, new Vector2(mark.X, mark.Y), StateLook.Accent);
            }

            c.Text(label, Vector2.Zero);
        }
    }

    private static Vector2 Body((double W, double H) size) => new((float)size.W, (float)size.H - PanelSpace.TabStripHeight);

    private static Vector2 BodyOrigin((double W, double H) size) => new((float)(-size.W / 2), PanelSpace.TabStripHeight);

    private static readonly ShelfItem[] ShelfItems =
    [
        new(1, "Q3-report.pdf", @"C:\q\Q3-report.pdf", ShelfKind.Reference, default) { Size = 2_400_000 },
        new(2, "IMG_0412.jpg", @"C:\q\IMG_0412.jpg", ShelfKind.Reference, default) { Size = 3_100_000 },
        new(3, "Assets", @"C:\q\Assets", ShelfKind.Reference, default) { IsFolder = true },
        new(4, "design-notes.txt", @"C:\q\design-notes.txt", ShelfKind.Reference, default) { Missing = true },
        new(5, "a-long-name-for-the-invoice-of-october.docx", @"C:\q\invoice.docx", ShelfKind.Copy, default) { Size = 88_000 },
    ];

    // The Shelf: head row, a page of tiles (the second one selected, with its buttons as dark squares), or the empty box.
    private static void Shelf(Canvas c, ShelfItem[] items)
    {
        var size = Body(PanelSize.Shelf);
        var body = BodyOrigin(PanelSize.Shelf);
        c.Pill();
        c.Mascot();
        Tabs(c, PanelId.Shelf, PanelSize.Shelf);
        foreach (var line in ShelfLayout.HeadText(size, items.Length))
        {
            c.Text(line, body);
        }

        if (items.Length == 0)
        {
            var empty = new RectangleF(ShelfLayout.PadX, ShelfLayout.GridTop, size.X - 2 * ShelfLayout.PadX, size.Y - ShelfLayout.GridTop - ShelfLayout.PadY);
            c.StrokeRect(Offset(empty, body), 8, 0x2EFFFFFF);
            foreach (var line in ShelfLayout.EmptyText(empty.Size))
            {
                c.Text(line, body + new Vector2(empty.X, empty.Y));
            }

            return;
        }

        int visible = ShelfLayout.Rows(size) * ShelfLayout.Columns;
        for (int slot = 0; slot < visible && slot < items.Length; slot++)
        {
            var item = items[slot];
            var tile = ShelfLayout.Tile(slot, size);
            var origin = body + new Vector2(tile.X, tile.Y);
            var local = new RectangleF(0, 0, tile.Width, tile.Height);
            c.RoundRect(Offset(local, origin), 8, slot == 1 ? 0x1CFFFFFFu : 0x0FFFFFFFu, item.Missing ? 0.55f : 1);
            c.RoundRect(Offset(new RectangleF(ShelfLayout.TilePad, ShelfLayout.TilePad, ShelfLayout.Icon, ShelfLayout.Icon), origin), 7, 0xFF000000 | ShelfLayout.KindColor(item), item.Missing ? 0.55f : 1);
            if (slot == 1)
            {
                c.StrokeRect(Offset(local, origin), 8, 0xFF6FA8FF);
                foreach (var action in Enum.GetValues<ShelfAction>())
                {
                    c.RoundRect(Offset(ShelfLayout.ActionButton(action, local), origin), 5, 0x73000000);
                }
            }

            foreach (var line in ShelfLayout.TileText(item, tile.Size))
            {
                c.Text(line, origin, item.Missing ? 0.55f : 1);
            }
        }
    }

    // The Shelf-drop surface: the dashed target right of the tomte with the folder glyph and its word.
    private static void ShelfDrop(Canvas c)
    {
        var g = IslandGeometry.ShelfDrop;
        c.Pill();
        c.Mascot();
        var target = ShelfLayout.DropTarget(g.Width, g.Depth);
        c.StrokeRect(target, 8, 0x59FFFFFF);
        var (x, label) = ShelfLayout.DropLabel(target.Size);
        float y = target.Height / 2;
        var origin = new Vector2(target.X, target.Y);
        c.StrokeRect(Offset(new RectangleF(x, y - 7, 7, 4), origin), 1.5f, 0x99FFFFFF);
        c.StrokeRect(Offset(new RectangleF(x, y - 5, 16, 12), origin), 2, 0x99FFFFFF);
        c.Text(label, origin);
    }

    // The Shortcuts grid with a script tile that may not run, a long name and the add tile; grey squares for icons.
    private static void Shortcuts(Canvas c)
    {
        var size = Body(PanelSize.Shortcuts);
        var body = BodyOrigin(PanelSize.Shortcuts);
        c.Pill();
        c.Mascot();
        Tabs(c, PanelId.Shortcuts, PanelSize.Shortcuts);
        foreach (var line in ShortcutGrid.HeadText(size))
        {
            c.Text(line, body);
        }

        (string Name, ShortcutOutcome? Why)[] tiles =
            [("VS Code", null), ("Terminal", null), ("Downloads", null), ("Figma file", null), ("build.ps1", ShortcutOutcome.ScriptsOff), ("Quarterly report draft (final, really)", null), ("old-notes.txt", ShortcutOutcome.Missing)];
        for (int i = 0; i <= tiles.Length && i < ShortcutGrid.Visible(0, tiles.Length, size).Count; i++)
        {
            var cell = ShortcutGrid.Cell(i, size);
            var holder = body + new Vector2(cell.X, cell.Y);
            var icon = ShortcutGrid.Icon(cell, c.Scale, PanelSpace.TabStripHeight);
            if (i == tiles.Length)
            {
                c.StrokeRect(Offset(cell, body), ShortcutGrid.Corner, 0x40FFFFFF);
                c.StrokeRect(Offset(icon, body), 8, 0x40FFFFFF);
                c.Text(ShortcutGrid.AddText(cell), holder);
                break;
            }

            c.RoundRect(Offset(cell, body), ShortcutGrid.Corner, 0x0FFFFFFF);
            c.RoundRect(Offset(icon, body), 8, 0x1CFFFFFF);
            foreach (var line in ShortcutGrid.TileText(cell, tiles[i].Name, tiles[i].Why))
            {
                c.Text(line, holder);
            }
        }
    }

    // The Scratchpad: heading, the note's card with the note in its text box and the caret after it, the footer.
    private static void Scratchpad(Canvas c, string note)
    {
        var layout = new ScratchpadPanelLayout(Body(PanelSize.Scratchpad));
        var body = BodyOrigin(PanelSize.Scratchpad);
        c.Pill();
        c.Mascot();
        Tabs(c, PanelId.Scratchpad, PanelSize.Scratchpad);
        foreach (var line in layout.FrameText())
        {
            c.Text(line, body);
        }

        c.RoundRect(Offset(layout.Note, body), ScratchpadPanelLayout.Corner, 0x0FFFFFFF);
        using var editor = Typing.Captured(note, ScratchpadPanelLayout.NoteSize, layout.Editor, c.Scale, note.Length, markdown: true);
        c.Editor(editor, layout.Editor, body);
    }

    private const string MarkdownNote = "# Saturday\n- [x] Milk and **eggs**\n- [ ] Bread, ~~rye~~ the *sourdough* one from the bakery on the corner, before it sells out at ten\n"
        + "\t- if it is gone, the rye\n## Calls\n1. Dentist `555 0100`, see [the clinic](https://example.com/clinic)\n```\ngit pull --rebase\n```";

    private static (string, bool)[] Chips(bool selection) =>
    [
        (selection ? "Selection · \"Tomte is a Windows 11 utility...\"" : "Selection · none", selection),
        ("Window · notepad · tomte-notes.txt - Notepad", false),
        ("Screenshot of window", false),
        ("Shelf · Q3-report.pdf", false),
        ("Scratchpad", false),
    ];

    private static readonly (string, TextStyle)[] Streaming =
    [
        ("Read the selection", PaletteLayout.StepText),
        ("Tomte is a Windows utility that stays out of sight until you call it from the top edge of the screen. It holds a file shelf, shortcuts, media controls and a scratchpad, and an optional AI layer on your own key, and it", PaletteLayout.AnswerText),
    ];

    private static readonly (string, TextStyle)[] Long =
    [
        ("Read the window title", PaletteLayout.StepText),
        (string.Join("\n\n", [
            "1. Restart the PC once. Many stuck updates finish on their own after a restart, because a file the update needs was in use.",
            "2. Run the Windows Update troubleshooter: Settings, System, Troubleshoot, Other troubleshooters, Windows Update. It resets the update services and clears common errors.",
            "3. Make room on the system drive. An update needs several gigabytes free; Settings, System, Storage shows what can go.",
            "4. Pause updates for a week and resume them. Resuming makes Windows look for the update again from the start.",
            "5. Clear the update cache. Stop the Windows Update service, delete the contents of C:\\Windows\\SoftwareDistribution\\Download, then start the service again.",
            "6. Install the update by hand. The Microsoft Update Catalog has every cumulative update; search for its KB number and run the installer.",
            "7. If none of this works, check the update history for the error code and search for it; most codes point to one cause.",
        ]), PaletteLayout.AnswerText),
    ];

    private static readonly (string, TextStyle)[] Asked = [("I can open it in your browser.", PaletteLayout.AnswerText)];

    private static readonly Confirmation Asking = new("Open https://example.com/tomte/releases", Confirmation.LineFor(Tomte.Core.Device.ActionTier.ExternalEffect, ConfirmKeys.Prompt), AlwaysAllow: true, Destructive: false);

    private static readonly Confirmation PowerAsking = new("Shut down the PC", Confirmation.LineFor(Tomte.Core.Device.ActionTier.Destructive, ConfirmKeys.Voice), AlwaysAllow: false, Destructive: true);

    // The palette's height with a question open, from the prompt's own height (the text box's at 150%, or what was
    // said by voice) and the answer, as the view lays it out.
    private static double AskHeight(Confirmation confirm, bool voice)
    {
        var first = new PaletteLayout((float)PanelSize.PaletteWidth, mascot: true);
        float text;
        if (voice)
        {
            using var said = TextLayout.Of(new TextRun("Turn the computer off", PaletteLayout.PromptText, first.Editor.Width, PaletteLayout.PromptLines));
            text = said.Height;
        }
        else
        {
            using var probe = Typing.Captured("Open the Tomte repository", PaletteLayout.PromptSize, first.Editor, 1.5, null);
            text = (float)(probe.ContentHeight / 1.5);
        }

        var layout = new PaletteLayout((float)PanelSize.PaletteWidth, mascot: true, text, voice ? [] : Chips(false), confirm);
        return layout.Height(voice ? 0 : layout.Answer(Asked).Height);
    }

    // The palette's height for the streaming scene, from the prompt's own height at 150% and the answer.
    private static double StreamingHeight()
    {
        var first = new PaletteLayout((float)PanelSize.PaletteWidth, mascot: true);
        using var probe = Typing.Captured("Summarize the selected text\nin two lines", PaletteLayout.PromptSize, first.Editor, 1.5, null);
        var layout = new PaletteLayout((float)PanelSize.PaletteWidth, mascot: true, probe.ContentHeight / 1.5f, Chips(true));
        return layout.Height(layout.Answer(Streaming).Height);
    }

    // The palette: the prompt's card with its text box (or the AI-off line), the chips, the answer and the footer, as
    // the palette view lays them out. A null prompt is a palette with no provider. The answer shows in its band, clipped
    // there and scrolled to <paramref name="scrolled"/> of its range (1 is the end, where a streaming answer stays).
    private static void Palette(Canvas c, string? prompt, (string Label, bool On)[] chips, string provider, (string Text, TextStyle Style)[] answer, int steps, decimal spent, float scrolled = 1, Confirmation? confirm = null, bool voice = false)
    {
        var origin = new Vector2((float)(-PanelSize.PaletteWidth / 2), 0);
        c.Pill();
        c.Mascot();
        float text = 0;
        TextServices? editor = null;
        var first = new PaletteLayout((float)PanelSize.PaletteWidth, mascot: true);
        if (prompt is not null && voice)
        {
            // By voice there is no text box: what was said shows in its place.
            using var said = TextLayout.Of(new TextRun(prompt, PaletteLayout.PromptText, first.Editor.Width, PaletteLayout.PromptLines));
            text = said.Height;
        }
        else if (prompt is not null)
        {
            editor = Typing.Captured(prompt, PaletteLayout.PromptSize, first.Editor, c.Scale, prompt.Length);
            text = (float)(editor.ContentHeight / c.Scale);
        }

        // As the view does it: the prompt's height first, then the box grows to it and the rest follows.
        var layout = new PaletteLayout((float)PanelSize.PaletteWidth, mascot: true, text, chips, confirm);
        var card = layout.Prompt;
        c.RoundRect(Offset(card, origin), PaletteLayout.PromptCorner, 0x0FFFFFFF);
        c.StrokeRect(Offset(card, origin), PaletteLayout.PromptCorner, 0x1AFFFFFF);
        var box = layout.Editor;
        if (prompt is null)
        {
            c.Text(PaletteLayout.Off, PaletteLayout.Placeholder, new RectangleF(box.X, card.Y, box.Width, card.Height), origin);
            c.Text(new TextRun(PaletteLayout.Setup, TextStyle.Secondary, layout.AnswerWidth), new Vector2(PaletteLayout.Pad, layout.ChipsTop + 4), origin);
            return;
        }

        if (editor is null)
        {
            c.Text(new TextRun(prompt, PaletteLayout.PromptText, box.Width, PaletteLayout.PromptLines), new Vector2(box.X, box.Y), origin);
        }
        else
        {
            editor.Resize(PixelGrid.Cover(box.Width, c.Scale), PixelGrid.Cover(box.Height, c.Scale), EditorScale.Zoom(c.Scale, TextServices.SystemDpi));
            c.Editor(editor, box, origin);
            editor.Dispose();
        }
        foreach (var (pill, label) in layout.Chips)
        {
            c.RoundRect(Offset(pill, origin), PaletteLayout.ChipCorner, label.Style == PaletteLayout.ChipOn ? 0x336FA8FFu : 0x0FFFFFFFu);
            c.Text(label, origin);
        }

        var (runs, measured) = layout.Answer(answer);
        float height = layout.Height(measured);
        var band = layout.AnswerBand(height);
        float scroll = layout.MaxScroll(measured, height) * scrolled;
        c.Clip = Offset(band, origin);
        foreach (var (run, at) in runs)
        {
            c.Text(run, new Vector2(at.X, band.Y + at.Y - scroll), origin);
        }

        c.Clip = null;

        // The question, with the view's colours: the chip blue round the card, Run filled white, or amber for what asks every time.
        if (layout.Ask(height) is { } ask)
        {
            c.RoundRect(Offset(ask.Card, origin), PaletteLayout.PromptCorner, 0x0FFFFFFF);
            c.StrokeRect(Offset(ask.Card, origin), PaletteLayout.PromptCorner, 0x666FA8FF);
            c.Text(ask.Summary, ask.SummaryAt, origin);
            c.Text(ask.Line, origin);
            foreach (var (button, rect, label) in ask.Buttons)
            {
                c.RoundRect(Offset(rect, origin), PaletteLayout.ButtonCorner, button != ConfirmButton.Run ? 0x1AFFFFFFu : confirm!.Destructive ? 0xFFF0B35Au : 0xFFFFFFFFu);
                c.Text(label, origin);
            }
        }

        foreach (var line in layout.Foot(provider, steps, spent, 2m, height))
        {
            c.Text(line, origin);
        }
    }

    private static readonly (string Display, string Ago)[] Rows =
    [
        ("Pull up last quarter's numbers, compare them with this one", "2 min"),
        ("learn.microsoft.com/windows/win32/api/dwmapi", "9 min"),
        ("WS_EX_NOREDIRECTIONBITMAP | WS_EX_TOPMOST | WS_EX_TOOLWINDOW", "31 min"),
        ("Meet at 10, not 9", "1 h"),
        ("Screenshot of the shelf, 1280 x 720", "3 d"),
    ];

    // The Clipboard history: on with rows, on and empty, or off.
    private static void Clipboard(Canvas c, bool on, (string Display, string Ago)[] rows)
    {
        var layout = new ClipboardPanelLayout(Body(PanelSize.Clipboard));
        var body = BodyOrigin(PanelSize.Clipboard);
        c.Pill();
        c.Mascot();
        Tabs(c, PanelId.Clipboard, PanelSize.Clipboard);
        foreach (var line in layout.HeadText(on, 50))
        {
            c.Text(line, body);
        }

        var track = Offset(layout.Switch, body);
        c.RoundRect(track, track.Height / 2, on ? 0xFF6FA8FFu : 0x33FFFFFFu);
        c.Circle(new Vector2(on ? track.Right - 9 : track.X + 9, track.Y + track.Height / 2), 7, 0xFFFFFFFF);
        if (!on || rows.Length == 0)
        {
            c.StrokeRect(Offset(layout.List, body), ClipboardPanelLayout.Corner, 0x2EFFFFFF);
            foreach (var line in layout.BoxText(on, 50))
            {
                c.Text(line, body);
            }

            return;
        }

        foreach (var line in layout.FootText())
        {
            c.Text(line, body);
        }

        var list = layout.List;
        for (int i = 0; i < rows.Length; i++)
        {
            var row = ClipboardPanelLayout.Row(i, list.Width);
            if (row.Bottom > list.Height)
            {
                break;
            }

            var origin = body + new Vector2(list.X + row.X, list.Y + row.Y);
            c.RoundRect(Offset(new RectangleF(0, 0, row.Width, row.Height), origin), ClipboardPanelLayout.Corner, 0x0FFFFFFF);
            float iconY = (row.Height - ClipboardPanelLayout.Icon) / 2;
            c.RoundRect(Offset(new RectangleF(ClipboardPanelLayout.Gap, iconY, ClipboardPanelLayout.Icon, ClipboardPanelLayout.Icon), origin), 6, 0x1CFFFFFF);
            var remove = Offset(ClipboardPanelLayout.RemoveIn(row.Width), origin);
            c.Circle(new Vector2(remove.X + remove.Width / 2, remove.Y + remove.Height / 2), remove.Width / 2, 0x0FFFFFFF);
            foreach (var line in ClipboardPanelLayout.RowText(rows[i].Display, rows[i].Ago, row.Width))
            {
                c.Text(line, origin);
            }
        }
    }

    private static RectangleF Offset(RectangleF r, Vector2 by) => new(r.X + by.X, r.Y + by.Y, r.Width, r.Height);

    /// <summary>
    /// One capture: the pill centred with room around it, island space starting on a whole pixel as on screen.
    /// Shapes go straight onto the canvas; each text run goes into its own transparent surface first.
    /// </summary>
    private sealed class Canvas : IDisposable
    {
        private const float Room = 24;
        private readonly ID2D1DeviceContext* dc;
        private readonly ID2D1Factory* factory;
        private readonly ID2D1Bitmap1* target;
        private readonly IslandGeometry g;
        private readonly double scale;
        private readonly int width, height;
        private readonly Vector2 origin;
        private readonly List<(byte[] Pixels, int Width, int Height, Vector2 At, float Opacity, RectangleF? Clip)> runs = [];

        public Canvas(ID2D1DeviceContext* dc, IslandGeometry g, double scale)
        {
            this.dc = dc;
            this.g = g;
            this.scale = scale;
            ID2D1Factory* f;
            dc->GetFactory(&f);
            factory = f;
            width = PixelGrid.Cover((float)g.Width + 2 * Room, scale);
            height = PixelGrid.Cover((float)g.Depth + 2 * Room, scale);
            // Island space: x = 0 on the pill's centre line, y = 0 at its top, both on whole pixels.
            origin = new Vector2(width / 2, (int)Math.Round(Room * scale)) / (float)scale;
            target = Bitmap(dc, width, height, scale, D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_TARGET | D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_CANNOT_DRAW);
            dc->SetTarget((ID2D1Image*)target);
            dc->SetDpi((float)(96 * scale), (float)(96 * scale));
            dc->BeginDraw();
            var back = Color(0xFF15161C);
            dc->Clear(&back);
            var move = new D2D_MATRIX_3X2_F { m11 = 1, m22 = 1, dx = origin.X, dy = origin.Y };
            dc->SetTransform(&move);
        }

        /// <summary>The pill: outline, vertical gradient #101012 to black, and a one-pixel hairline at 10% white.</summary>
        public void Pill()
        {
            var segments = new List<OutlineSegment>();
            IslandOutline.Build(g, false, segments);
            var path = Path(segments.ToArray(), new Vector2((float)(-g.Width / 2), 0), 1);
            D2D1_GRADIENT_STOP* stops = stackalloc D2D1_GRADIENT_STOP[2];
            stops[0] = new D2D1_GRADIENT_STOP { position = 0, color = Color(0xFF101012) };
            stops[1] = new D2D1_GRADIENT_STOP { position = 1, color = Color(0xFF000000) };
            ID2D1GradientStopCollection* collection;
            dc->CreateGradientStopCollection(stops, 2, D2D1_GAMMA.D2D1_GAMMA_2_2, D2D1_EXTEND_MODE.D2D1_EXTEND_MODE_CLAMP, &collection);
            var line = new D2D1_LINEAR_GRADIENT_BRUSH_PROPERTIES { startPoint = new D2D_POINT_2F { x = 0, y = 0 }, endPoint = new D2D_POINT_2F { x = 0, y = (float)g.Depth } };
            ID2D1LinearGradientBrush* gradient;
            dc->CreateLinearGradientBrush(&line, null, collection, &gradient);
            dc->FillGeometry((ID2D1Geometry*)path, (ID2D1Brush*)gradient, null);
            var hairline = Brush(0x1AFFFFFF);
            dc->DrawGeometry((ID2D1Geometry*)path, (ID2D1Brush*)hairline, (float)(1 / scale), null);
            hairline->Release();
            gradient->Release();
            collection->Release();
            path->Release();
        }

        /// <summary>The tomte at rest, in its box at the pill's left edge.</summary>
        public void Mascot()
        {
            var box = MascotArt.Box(g);
            var at = new Vector2(box.X, box.Y);
            float k = MascotArt.SizeDips / MascotArt.ViewBox;
            uint cap = 0xFF000000 | MascotArt.CapRed, shade = 0xFF000000 | MascotArt.Shade(MascotArt.CapRed);
            Fill(MascotArt.Beard, at, k, 0xFF000000 | MascotArt.BeardColor);
            Fill(MascotArt.BeardShade, at, k, 0xFF000000 | MascotArt.BeardShadeColor);
            Circle(at + MascotArt.Nose.Center * k, MascotArt.Nose.Radius * k, 0xFF000000 | MascotArt.NoseColor);
            Fill(MascotArt.CapCone, at, k, cap);
            Fill(MascotArt.TipCurl, at, k, cap);
            Circle(at + MascotArt.TipBulb.Center * k, MascotArt.TipBulb.Radius * k, cap);
            Fill(MascotArt.Brim, at, k, shade);
        }

        public void RoundRect(RectangleF r, float radius, uint argb, float opacity = 1)
        {
            var brush = Brush(argb);
            brush->SetOpacity(opacity);
            var round = new D2D1_ROUNDED_RECT { rect = Rect(r), radiusX = radius, radiusY = radius };
            dc->FillRoundedRectangle(&round, (ID2D1Brush*)brush);
            brush->Release();
        }

        /// <summary>Stand-in album art: a diagonal gradient.</summary>
        public void Gradient(RectangleF r, float radius, uint from, uint to)
        {
            D2D1_GRADIENT_STOP* stops = stackalloc D2D1_GRADIENT_STOP[2];
            stops[0] = new D2D1_GRADIENT_STOP { position = 0, color = Color(from) };
            stops[1] = new D2D1_GRADIENT_STOP { position = 1, color = Color(to) };
            ID2D1GradientStopCollection* collection;
            dc->CreateGradientStopCollection(stops, 2, D2D1_GAMMA.D2D1_GAMMA_2_2, D2D1_EXTEND_MODE.D2D1_EXTEND_MODE_CLAMP, &collection);
            var line = new D2D1_LINEAR_GRADIENT_BRUSH_PROPERTIES { startPoint = new D2D_POINT_2F { x = r.Left, y = r.Top }, endPoint = new D2D_POINT_2F { x = r.Right, y = r.Bottom } };
            ID2D1LinearGradientBrush* gradient;
            dc->CreateLinearGradientBrush(&line, null, collection, &gradient);
            var round = new D2D1_ROUNDED_RECT { rect = Rect(r), radiusX = radius, radiusY = radius };
            dc->FillRoundedRectangle(&round, (ID2D1Brush*)gradient);
            gradient->Release();
            collection->Release();
        }

        public void Circle(Vector2 centre, float radius, uint argb)
        {
            var brush = Brush(argb);
            var ellipse = new D2D1_ELLIPSE { point = new D2D_POINT_2F { x = centre.X, y = centre.Y }, radiusX = radius, radiusY = radius };
            dc->FillEllipse(&ellipse, (ID2D1Brush*)brush);
            brush->Release();
        }

        /// <summary>The timer ring: a 14% white track and a green arc clockwise from 12 o'clock.</summary>
        public void Ring(Vector2 centre, float radius, float stroke, float fill)
        {
            var track = Brush(0x24FFFFFF);
            var ellipse = new D2D1_ELLIPSE { point = new D2D_POINT_2F { x = centre.X, y = centre.Y }, radiusX = radius, radiusY = radius };
            dc->DrawEllipse(&ellipse, (ID2D1Brush*)track, stroke, null);
            track->Release();
            var green = Brush(0xFF4CC38A);
            if (fill >= 1)
            {
                dc->DrawEllipse(&ellipse, (ID2D1Brush*)green, stroke, null);
            }
            else if (fill > 0)
            {
                ID2D1PathGeometry* arc;
                factory->CreatePathGeometry(&arc);
                ID2D1GeometrySink* sink;
                arc->Open(&sink);
                sink->BeginFigure(new D2D_POINT_2F { x = centre.X, y = centre.Y - radius }, D2D1_FIGURE_BEGIN.D2D1_FIGURE_BEGIN_HOLLOW);
                double angle = 2 * Math.PI * fill;
                var segment = new D2D1_ARC_SEGMENT
                {
                    point = new D2D_POINT_2F { x = centre.X + radius * (float)Math.Sin(angle), y = centre.Y - radius * (float)Math.Cos(angle) },
                    size = new D2D_SIZE_F { width = radius, height = radius },
                    sweepDirection = D2D1_SWEEP_DIRECTION.D2D1_SWEEP_DIRECTION_CLOCKWISE,
                    arcSize = fill > 0.5f ? D2D1_ARC_SIZE.D2D1_ARC_SIZE_LARGE : D2D1_ARC_SIZE.D2D1_ARC_SIZE_SMALL,
                };
                sink->AddArc(&segment);
                sink->EndFigure(D2D1_FIGURE_END.D2D1_FIGURE_END_OPEN);
                sink->Close();
                sink->Release();
                dc->DrawGeometry((ID2D1Geometry*)arc, (ID2D1Brush*)green, stroke, null);
                arc->Release();
            }

            green->Release();
        }

        /// <summary>A transport glyph from its 24-unit box, 14 DIPs wide and centred in its button.</summary>
        public void Glyph(RectangleF button, uint argb, RectangleF[] bars, Vector2[][] triangles)
        {
            const float k = 14f / 24;
            var at = new Vector2(button.X + (button.Width - 14) / 2, button.Y + (button.Height - 14) / 2);
            foreach (var bar in bars)
            {
                RoundRect(new RectangleF(at.X + bar.X * k, at.Y + bar.Y * k, bar.Width * k, bar.Height * k), 0, argb);
            }

            foreach (var t in triangles)
            {
                Fill([OutlineSegment.Move(t[0]), OutlineSegment.Line(t[1]), OutlineSegment.Line(t[2])], at, k, argb);
            }
        }

        /// <summary>The Ask pill's sparkle, scaled from its box to fill <paramref name="glyph"/>'s height.</summary>
        public void Sparkle(RectangleF glyph, uint argb)
        {
            foreach (var star in TabStrip.Sparkle)
            {
                Fill(star, new Vector2(glyph.X, glyph.Y), glyph.Height / TabStrip.SparkleBox.Y, argb);
            }
        }

        /// <summary>
        /// One line in a slot of a host, as the island's text visual places it: as wide as the slot, centred in its
        /// height, snapped to the pixel grid, on a surface the text's size plus the pad.
        /// </summary>
        public void Text(string text, TextStyle style, RectangleF slot, Vector2 host, float opacity = 1) => Text(new TextRun(text, style, slot.Width), slot, host, centred: true, opacity);

        public void Text(TextSlot line, Vector2 host, float opacity = 1) => Text(line.Text, line.Style, line.Slot, host, opacity);

        /// <summary>A run of any number of lines with its box's top-left corner at a point of the host.</summary>
        public void Text(TextRun run, Vector2 at, Vector2 host) => Text(run, new RectangleF(at.X, at.Y, run.MaxWidth, 0), host, centred: false, 1);

        public double Scale => scale;

        /// <summary>Where the runs added from now on are cut off, as a container's clip cuts them; null for none.</summary>
        public RectangleF? Clip { get; set; }

        /// <summary>
        /// A text box as the island shows it: the services drawn into a transparent surface of the box's size in whole
        /// pixels at the box's snapped corner, and the caret as a white sprite where the services put it, the user's
        /// caret width times the scale.
        /// </summary>
        public void Editor(TextServices editor, RectangleF box, Vector2 host)
        {
            int w = PixelGrid.Cover(box.Width, scale), h = PixelGrid.Cover(box.Height, scale);
            ID2D1Image* previous;
            dc->GetTarget(&previous);
            D2D_MATRIX_3X2_F transform;
            dc->GetTransform(&transform);
            dc->EndDraw(null, null);
            var surface = Bitmap(dc, w, h, scale, D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_TARGET | D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_CANNOT_DRAW);
            dc->SetTarget((ID2D1Image*)surface);
            dc->BeginDraw();
            editor.Draw(dc, new Point(0, 0), null);
            dc->EndDraw(null, null);
            var pixels = Read(dc, surface, w, h, scale);
            surface->Release();
            dc->SetTarget(previous);
            previous->Release();
            dc->SetDpi((float)(96 * scale), (float)(96 * scale));
            dc->BeginDraw();
            dc->SetTransform(&transform);
            var at = new Vector2(host.X + PixelGrid.Snap(host.X, box.X, scale), host.Y + PixelGrid.Snap(host.Y, box.Y, scale));
            runs.Add((pixels, w, h, at, 1, Clip));
            var (x, y, caretHeight, shown) = editor.Caret;
            if (shown && caretHeight > 0)
            {
                uint width = 1;
                PInvoke.SystemParametersInfo(Windows.Win32.UI.WindowsAndMessaging.SYSTEM_PARAMETERS_INFO_ACTION.SPI_GETCARETWIDTH, 0, &width, 0);
                int cw = Math.Max(1, (int)Math.Round(width * scale));
                var caret = new byte[cw * caretHeight * 4];
                Array.Fill(caret, (byte)255);
                runs.Add((caret, cw, caretHeight, at + new Vector2((float)(x / scale), (float)(y / scale)), 1, Clip));
            }
        }

        /// <summary>A rounded outline this many DIPs wide, centred on the rectangle's edge, as a composition stroke is.</summary>
        public void Stroke(RectangleF r, float radius, uint argb, float width)
        {
            var brush = Brush(argb);
            var round = new D2D1_ROUNDED_RECT { rect = Rect(r), radiusX = radius, radiusY = radius };
            dc->DrawRoundedRectangle(&round, (ID2D1Brush*)brush, width, null);
            brush->Release();
        }

        /// <summary>A filled figure in DIPs, its origin at <paramref name="at"/>.</summary>
        public void Shape(ReadOnlySpan<OutlineSegment> segments, Vector2 at, uint argb) => Fill(segments, at, 1, argb);

        /// <summary>A rounded outline one pixel wide, inside the rectangle.</summary>
        public void StrokeRect(RectangleF r, float radius, uint argb)
        {
            var brush = Brush(argb);
            float half = (float)(0.5 / scale);
            var round = new D2D1_ROUNDED_RECT { rect = Rect(new RectangleF(r.X + half, r.Y + half, r.Width - 2 * half, r.Height - 2 * half)), radiusX = radius, radiusY = radius };
            dc->DrawRoundedRectangle(&round, (ID2D1Brush*)brush, (float)(1 / scale), null);
            brush->Release();
        }

        private void Text(TextRun run, RectangleF slot, Vector2 host, bool centred, float opacity)
        {
            var watch = Stopwatch.StartNew();
            using var block = new TextBlock();
            block.Set(run, scale);
            LayoutMs.Add(watch.Elapsed.TotalMilliseconds);
            if (block.Layout is not { } layout)
            {
                return;
            }

            float top = centred ? slot.Y + (slot.Height - layout.Height) / 2 : slot.Y;
            float x = PixelGrid.Snap(host.X, slot.X, scale), y = PixelGrid.Snap(host.Y, top, scale);
            float pad = (float)(TextBlock.Pad / scale);
            var (w, h) = block.Pixels;
            watch.Restart();
            var pixels = Render(dc, w, h, scale, layout, new Vector2(pad));
            DrawMs.Add(watch.Elapsed.TotalMilliseconds);
            var at = new Vector2(host.X + x - pad, host.Y + y - pad);
            var pixel = (origin + at) * (float)scale;
            if (Math.Abs(pixel.X - Math.Round(pixel.X)) > 0.002 || Math.Abs(pixel.Y - Math.Round(pixel.Y)) > 0.002)
            {
                OffGrid++;
            }

            runs.Add((pixels, w, h, at, opacity, Clip));
        }

        /// <summary>A run alone on a transparent surface of its own, as the island draws it, read back as pixels.</summary>
        public static byte[] Render(ID2D1DeviceContext* dc, int width, int height, double scale, TextLayout layout, Vector2 at)
        {
            ID2D1Image* previous;
            dc->GetTarget(&previous);
            var surface = Bitmap(dc, width, height, scale, D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_TARGET | D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_CANNOT_DRAW);
            D2D_MATRIX_3X2_F transform;
            dc->GetTransform(&transform);
            bool drawing = previous is not null;
            if (drawing)
            {
                dc->EndDraw(null, null);
            }

            dc->SetTarget((ID2D1Image*)surface);
            dc->SetDpi((float)(96 * scale), (float)(96 * scale));
            dc->BeginDraw();
            var identity = new D2D_MATRIX_3X2_F { m11 = 1, m22 = 1 };
            dc->SetTransform(&identity);
            var clear = default(D2D1_COLOR_F);
            dc->Clear(&clear);
            TextDraw.Run(dc, layout, at);
            dc->EndDraw(null, null);
            var pixels = Read(dc, surface, width, height, scale);
            surface->Release();
            dc->SetTarget(previous);
            if (drawing)
            {
                previous->Release();
                dc->BeginDraw();
                dc->SetTransform(&transform);
            }

            return pixels;
        }

        public void Save(string file)
        {
            // The runs go over everything else, as the island's text sprites sit on top in their hosts.
            foreach (var (pixels, w, h, at, opacity, clip) in runs)
            {
                if (clip is { } cut)
                {
                    var r = Rect(cut);
                    dc->PushAxisAlignedClip(&r, D2D1_ANTIALIAS_MODE.D2D1_ANTIALIAS_MODE_ALIASED);
                }

                var props = new D2D1_BITMAP_PROPERTIES1 { pixelFormat = Format, dpiX = (float)(96 * scale), dpiY = (float)(96 * scale) };
                ID2D1Bitmap1* run;
                fixed (byte* bits = pixels)
                {
                    dc->CreateBitmap(new D2D_SIZE_U { width = (uint)w, height = (uint)h }, bits, (uint)w * 4, &props, &run);
                }

                var dest = new D2D_RECT_F { left = at.X, top = at.Y, right = at.X + (float)(w / scale), bottom = at.Y + (float)(h / scale) };
                dc->DrawBitmap((ID2D1Bitmap*)run, &dest, opacity, D2D1_INTERPOLATION_MODE.D2D1_INTERPOLATION_MODE_NEAREST_NEIGHBOR, null, null);
                run->Release();
                if (clip is not null)
                {
                    dc->PopAxisAlignedClip();
                }
            }

            dc->EndDraw(null, null);
            Png(file, Read(dc, target, width, height, scale), width, height);
        }

        public void Dispose()
        {
            dc->SetTarget(null);
            target->Release();
            factory->Release();
        }

        private static readonly D2D1_PIXEL_FORMAT Format = new() { format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, alphaMode = D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_PREMULTIPLIED };

        private static ID2D1Bitmap1* Bitmap(ID2D1DeviceContext* dc, int width, int height, double scale, D2D1_BITMAP_OPTIONS options)
        {
            var props = new D2D1_BITMAP_PROPERTIES1 { pixelFormat = Format, dpiX = (float)(96 * scale), dpiY = (float)(96 * scale), bitmapOptions = options };
            ID2D1Bitmap1* bitmap;
            dc->CreateBitmap(new D2D_SIZE_U { width = (uint)width, height = (uint)height }, null, 0, &props, &bitmap);
            return bitmap;
        }

        private static byte[] Read(ID2D1DeviceContext* dc, ID2D1Bitmap1* from, int width, int height, double scale)
        {
            var cpu = Bitmap(dc, width, height, scale, D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_CPU_READ | D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_CANNOT_DRAW);
            cpu->CopyFromBitmap(null, (ID2D1Bitmap*)from, null);
            D2D1_MAPPED_RECT mapped;
            cpu->Map(D2D1_MAP_OPTIONS.D2D1_MAP_OPTIONS_READ, &mapped);
            var pixels = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                new ReadOnlySpan<byte>(mapped.bits + y * mapped.pitch, width * 4).CopyTo(pixels.AsSpan(y * width * 4));
            }

            cpu->Unmap();
            cpu->Release();
            return pixels;
        }

        private static void Png(string file, byte[] pixels, int width, int height)
        {
            using var stream = new InMemoryRandomAccessStream();
            var encoder = BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream).AsTask().GetAwaiter().GetResult();
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)width, (uint)height, 96, 96, pixels);
            encoder.FlushAsync().AsTask().GetAwaiter().GetResult();
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            reader.LoadAsync((uint)stream.Size).AsTask().GetAwaiter().GetResult();
            var bytes = new byte[stream.Size];
            reader.ReadBytes(bytes);
            File.WriteAllBytes(file, bytes);
        }

        private static D2D1_COLOR_F Color(uint argb) =>
            new() { r = ((argb >> 16) & 0xFF) / 255f, g = ((argb >> 8) & 0xFF) / 255f, b = (argb & 0xFF) / 255f, a = (argb >> 24) / 255f };

        private static D2D_RECT_F Rect(RectangleF r) => new() { left = r.Left, top = r.Top, right = r.Right, bottom = r.Bottom };

        private ID2D1SolidColorBrush* Brush(uint argb)
        {
            var color = Color(argb);
            ID2D1SolidColorBrush* brush;
            dc->CreateSolidColorBrush(&color, null, &brush);
            return brush;
        }

        private void Fill(ReadOnlySpan<OutlineSegment> segments, Vector2 at, float k, uint argb)
        {
            var path = Path(segments, at, k);
            var brush = Brush(argb);
            dc->FillGeometry((ID2D1Geometry*)path, (ID2D1Brush*)brush, null);
            brush->Release();
            path->Release();
        }

        private ID2D1PathGeometry* Path(ReadOnlySpan<OutlineSegment> segments, Vector2 at, float k)
        {
            ID2D1PathGeometry* path;
            factory->CreatePathGeometry(&path);
            ID2D1GeometrySink* sink;
            path->Open(&sink);
            bool open = false;
            foreach (var s in segments)
            {
                var end = Point(s.End);
                switch (s.Kind)
                {
                    case OutlineSegmentKind.Move:
                        if (open)
                        {
                            sink->EndFigure(D2D1_FIGURE_END.D2D1_FIGURE_END_CLOSED);
                        }

                        sink->BeginFigure(end, D2D1_FIGURE_BEGIN.D2D1_FIGURE_BEGIN_FILLED);
                        open = true;
                        break;
                    case OutlineSegmentKind.Line:
                        sink->AddLine(end);
                        break;
                    case OutlineSegmentKind.Quadratic:
                        var quad = new D2D1_QUADRATIC_BEZIER_SEGMENT { point1 = Point(s.Control1), point2 = end };
                        sink->AddQuadraticBezier(&quad);
                        break;
                    default:
                        var cubic = new D2D1_BEZIER_SEGMENT { point1 = Point(s.Control1), point2 = Point(s.Control2), point3 = end };
                        sink->AddBezier(&cubic);
                        break;
                }
            }

            if (open)
            {
                sink->EndFigure(D2D1_FIGURE_END.D2D1_FIGURE_END_CLOSED);
            }

            sink->Close();
            sink->Release();
            return path;

            D2D_POINT_2F Point(Vector2 p) => new() { x = at.X + p.X * k, y = at.Y + p.Y * k };
        }
    }
}
