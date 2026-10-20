using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using Tomte.Core;
using Tomte.Core.Clipboard;
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

        if (args is ["--composition", var into])
        {
            return CompositionCaptures(into);
        }

        if (args is ["--close", var closeInto])
        {
            return CloseCaptures(closeInto);
        }

        string dir = args.Length > 0 ? args[0] : "captures";
        Directory.CreateDirectory(dir);
        var report = new List<string> { $"machine {Environment.MachineName}, {Environment.OSVersion}" };
        report.Add(Memory("start"));

        var watch = Stopwatch.StartNew();
        using (var first = TextLayout.Of(new TextRun("Nightfall", MediaPanelLayout.TitleStyle, MediaLayout.Title.Width)))
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
        report.Add($"an italic word ending a run stays on the run's surface at 100%, 125% and 150%: {ItalicFits(dc)}");

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
        TextLayout.Of(new TextRun("Nightfall", MediaPanelLayout.TitleStyle, MediaLayout.Title.Width)).Dispose();
        GC.Collect();
        string before = Memory("before the devices");
        var d2d = TextDraw.CreateDevice(out ID3D11Device* d3d);
        ID2D1DeviceContext* dc;
        d2d->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS.D2D1_DEVICE_CONTEXT_OPTIONS_NONE, &dc);
        foreach (var (text, style, slot) in new[]
        {
            ("Nightfall", MediaPanelLayout.TitleStyle, MediaLayout.Title),
            ("Lo-fi radio · lofi beats to study", MediaPanelLayout.ArtistStyle, MediaLayout.Artist),
            ("00:48", MediaPanelLayout.ElapsedStyle, MediaLayout.Elapsed),
            ("03:15", MediaPanelLayout.TotalStyle, MediaLayout.Total),
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

    /// <summary>
    /// An italic word ending a run, drawn as the island draws a run: alone, at the pad, on the surface its block asks for,
    /// here with room to spare on the right, at each scale. No ink may fall in the spare room. The text font has no
    /// italic of its own, so DirectWrite slants it, and the slant leans past the line's width.
    /// </summary>
    private static bool ItalicFits(ID2D1DeviceContext* dc)
    {
        const int Spare = 16;
        foreach (double scale in Scales)
        {
            foreach (var run in new[]
            {
                new TextRun("Food", PaletteLayout.AnswerText, 600, 9) { Italic = [(0, 4)] },
                new TextRun("Click Update", DropResultLayout.BodyStyle, 600, 9) { Italic = [(6, 6)] },
            })
            {
                using var block = new TextBlock();
                block.Set(run, scale);
                var (w, h) = block.Pixels;
                var pixels = Canvas.Render(dc, w + Spare, h, scale, block.Layout!, new Vector2((float)(TextBlock.Pad / scale)));
                for (int y = 0; y < h; y++)
                {
                    for (int x = w; x < w + Spare; x++)
                    {
                        if (pixels[(y * (w + Spare) + x) * 4 + 3] != 0)
                        {
                            return false;
                        }
                    }
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
            new("media-next-refused", media, c => MediaPanel(c, playing with { Next = false }, null)),
            new("media-seek-hover", media, c => MediaPanel(c, playing, null, new Seeking(MediaLayout.Bar.X + 120, Drag: false))),
            new("media-seek-drag", media, c => MediaPanel(c, playing, null, new Seeking(MediaLayout.Bar.X + 120, Drag: true))),
            new("media-seek-drag-end", media, c => MediaPanel(c, playing, null, new Seeking(MediaLayout.Bar.Right + 20, Drag: true))),
            new("media-targets", media, c => MediaPanel(c, playing, null, targets: true)),
            // A panel cut to 60% of a 1280 x 800 screen at 150%: the bar and the end clock come in with the right edge.
            new("media-narrow-playing", IslandGeometry.Panel(NarrowMedia, PanelSize.Media.H), c => MediaPanel(c, playing, null, width: NarrowMedia)),
            new("media-narrow-targets", IslandGeometry.Panel(NarrowMedia, PanelSize.Media.H), c => MediaPanel(c, playing, null, targets: true, width: NarrowMedia)),
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
            UndoPill("compact-undo-answer", "AI answer closed", key: true),
            UndoPill("compact-undo-search", "AI search closed", key: true),
            UndoPill("compact-undo-result", "AI result closed", key: true),
            UndoPill("compact-undo-transcript", "Transcript closed", key: true),
            // Another app holds Win+Alt+Z: the button alone.
            UndoPill("compact-undo-no-key", "AI answer closed", key: false),
            new("compact-peek", IslandGeometry.Peek, c =>
            {
                c.Pill();
                c.Mascot();
            }),
            new("compact-rambler", IslandGeometry.Compact with { Width = 262 }, c => RamblerPill(c, mascot: true)),
            new("compact-kept-playing", IslandGeometry.Compact, c => NowPlayingRow(c, "Nightfall", bars: false)),
            new("compact-off-kept-playing", IslandGeometry.Compact, c => NowPlayingRow(c, "Nightfall", mascot: false, bars: false)),
            // The same pills with the tomte turned off in settings.
            new("compact-off-glance-date", IslandGeometry.Compact, c => CompactLine(c, Glance(new GlanceFacts(), mascot: false), ring: null, mascot: false)),
            new("compact-off-timer-running", IslandGeometry.Compact, c => CompactLine(c, CompactRules.Countdown(TimeSpan.FromSeconds(1499.4)), ring: 0.0004f, tabular: true, mascot: false)),
            new("compact-off-timer-done", IslandGeometry.Compact, c => CompactLine(c, CompactRules.TimerDone(TimeSpan.FromMinutes(25)), ring: 1, mascot: false)),
            new("compact-off-device-event", IslandGeometry.Compact, c => CompactLine(c, "Volume 40%.", ring: null, mascot: false)),
            new("compact-off-first-run-hint", IslandGeometry.Compact, c => CompactLine(c, CompactRules.Hint, ring: null, mascot: false)),
            new("compact-off-now-playing", IslandGeometry.Compact, c => NowPlayingRow(c, "Nightfall", mascot: false)),
            UndoPill("compact-off-undo-answer", "AI answer closed", key: true, mascot: false),
            new("compact-off-rambler", IslandGeometry.Compact with { Width = 262 }, c => RamblerPill(c, mascot: false)),
            new("strip-ask-widest", IslandGeometry.Panel(PanelSize.Shelf.W, 64), c => Strip(c, PanelId.Shelf, PanelSize.Shelf)),
            // Every panel is one width; a narrower strip is a panel cut to 60% of an 800 x 600 screen.
            new("strip-ask-narrowest", IslandGeometry.Panel(480, 64), c => Strip(c, PanelId.Media, (480, PanelSize.Media.H))),
            // The interaction host's looks over the strip: hover and pressed on a tab, the focus ring on the current one,
            // the Ask pill's tooltip, a "Copied" acknowledgement, and the search button's focus ring and tooltip.
            new("strip-hover", StripLooks, c => StripLook(c, layer: StateLook.Hover)),
            new("strip-pressed", StripLooks, c => StripLook(c, layer: StateLook.Pressed)),
            new("strip-focus", StripLooks, c => StripLook(c, ring: true)),
            new("strip-tip", StripLooks, c => StripLook(c, tip: "Ask Tomte anything")),
            new("strip-search-focus", StripLooks, c => StripLook(c, ring: true, search: true)),
            new("strip-search-tip", StripLooks, c => StripLook(c, tip: TabStrip.SearchTip(new HotkeySettings().Search), search: true)),
            new("strip-acknowledged", StripLooks, c => StripLook(c, tip: "Copied", tick: true)),
            new("shelf", Panel(PanelSize.Shelf), c => Shelf(c, ShelfItems, PanelSize.Shelf)),
            new("shelf-hover", Panel(PanelSize.Shelf), c => Shelf(c, ShelfItems, PanelSize.Shelf, owner: 2)),
            new("shelf-selected", Panel(PanelSize.Shelf), c => Shelf(c, ShelfItems, PanelSize.Shelf, owner: 1, selectedSlot: 1)),
            new("shelf-remove-confirm", Panel(PanelSize.Shelf), c => Shelf(c, ShelfItems, PanelSize.Shelf, owner: 1, confirming: true, selectedSlot: 1)),
            new("shelf-missing-hover", Panel(PanelSize.Shelf), c => Shelf(c, ShelfItems, PanelSize.Shelf, owner: 3)),
            new("shelf-narrow-hover", Panel(NarrowShelf), c => Shelf(c, ShelfItems, NarrowShelf, owner: 0)),
            new("shelf-narrow-remove-confirm", Panel(NarrowShelf), c => Shelf(c, ShelfItems, NarrowShelf, owner: 0, confirming: true)),
            new("shelf-empty", Panel(PanelSize.Shelf), c => Shelf(c, [], PanelSize.Shelf)),
            new("shelf-drop", IslandGeometry.ShelfDrop, ShelfDrop),
            new("shortcuts", Panel(PanelSize.Shortcuts), c => Shortcuts(c)),
            new("scratchpad", Panel(PanelSize.Scratchpad), c => Scratchpad(c, "Call Mom\nSubscribe to the newsletter about small tools, the one with the long name that wraps over two lines\nGym 6:00 PM")),
            new("scratchpad-markdown", Panel(PanelSize.Scratchpad), c => Scratchpad(c, MarkdownNote)),
            // Nothing selected, so no Selection chip; the rest off. Send is on for the typed question.
            new("palette-prompt", IslandGeometry.Panel(PanelSize.PaletteWidth, PaletteHeight("What does this error mean?", Chips(false), [])), c => Palette(c, "What does this error mean?", Chips(false), "Nothing sent", [], 0, 0.03m)),
            new("palette-empty", IslandGeometry.Panel(PanelSize.PaletteWidth, PaletteHeight("", Chips(false), [])), c => Palette(c, "", Chips(false), "Nothing sent", [], 0, 0.03m)),
            new("palette-chips-some-on", IslandGeometry.Panel(PanelSize.PaletteWidth, PaletteHeight("Summarize the selected text", Chips(true, ChipIcon.Selection, ChipIcon.Screenshot), [], sends: SomeOn)), c => Palette(c, "Summarize the selected text", Chips(true, ChipIcon.Selection, ChipIcon.Screenshot), "Nothing sent", [], 0, 0.03m, sends: SomeOn)),
            new("palette-region-empty", IslandGeometry.Panel(PanelSize.PaletteWidth, PaletteHeight("", CircledChips, [], sends: RegionOn)), c => Palette(c, "", CircledChips, "Nothing sent", [], 0, 0.03m, sends: RegionOn)),
            new("palette-region", IslandGeometry.Panel(PanelSize.PaletteWidth, PaletteHeight("Which game is this?", RegionChips(), [], sends: RegionOn)), c => Palette(c, "Which game is this?", RegionChips(), "Nothing sent", [], 0, 0.03m, sends: RegionOn)),
            // Sending: the chips and Send dim.
            new("palette-streaming", IslandGeometry.Panel(PanelSize.PaletteWidth, PaletteHeight("Summarize the selected text\nin two lines", Chips(true, ChipIcon.Selection), Streaming, sends: SelectionOn)), c => Palette(c, "Summarize the selected text\nin two lines", Chips(true, ChipIcon.Selection), "Sending to ollama · llama3.2:3b", Streaming, 1, 0.03m, sending: true, sends: SelectionOn)),
            new("palette-ai-off", IslandGeometry.Panel(PanelSize.PaletteWidth, PanelSize.PaletteMinHeight), c => Palette(c, null, [], "", [], 0, 0)),
            new("palette-long-answer-scrolled", IslandGeometry.Panel(PanelSize.PaletteWidth, PanelSize.PaletteMaxHeight), c => Palette(c, "How do I fix a stuck Windows update?", Chips(false), "Sent to ollama · llama3.2:3b", Long, 2, 0.04m, scrolled: 0.5f)),
            new("palette-answer", IslandGeometry.Panel(PanelSize.PaletteWidth, PaletteHeight("does this add up? one line", RegionChips(), Invoice, sends: RegionOn, actions: AnswerActions)), c => Palette(c, "does this add up? one line", RegionChips(), "Sent to Anthropic · claude-haiku-5-5", Invoice, 0, 0.12m, sends: RegionOn, actions: AnswerActions, cost: 0.002m)),
            new("palette-markdown", IslandGeometry.Panel(PanelSize.PaletteWidth, PaletteHeight("Where did my money go in March?", Chips(false), Marked)), c => Palette(c, "Where did my money go in March?", Chips(false), "Sent to ollama · llama3.2:3b", Marked, 1, 0.03m)),
            // A question opens during a send: Run dim while its read guard runs, then ready.
            new("palette-confirm-waiting", IslandGeometry.Panel(PanelSize.PaletteWidth, PaletteHeight("Open the Tomte repository", Chips(false), Asked, Asking)), c => Palette(c, "Open the Tomte repository", Chips(false), PaletteLayout.Waiting, Asked, 0, 0.03m, confirm: Asking, sending: true, run: StateLook.DisabledOpacity)),
            new("palette-confirm", IslandGeometry.Panel(PanelSize.PaletteWidth, PaletteHeight("Open the Tomte repository", Chips(false), Asked, Asking)), c => Palette(c, "Open the Tomte repository", Chips(false), PaletteLayout.Waiting, Asked, 0, 0.03m, confirm: Asking, sending: true)),
            new("palette-voice-confirm", IslandGeometry.Panel(PanelSize.PaletteWidth, PaletteHeight("Turn the computer off", [], [], PowerAsking, voice: true)), c => Palette(c, "Turn the computer off", [], PaletteLayout.Waiting, [], 0, 0.01m, confirm: PowerAsking, voice: true, sending: true)),
            new("clipboard-history", Panel(PanelSize.Clipboard), c => Clipboard(c, on: true, Rows)),
            new("clipboard-images", Panel(PanelSize.Clipboard), c => Clipboard(c, on: true, ImageRows.Value)),
            new("clipboard-off", Panel(PanelSize.Clipboard), c => Clipboard(c, on: false, [])),
            new("clipboard-empty", Panel(PanelSize.Clipboard), c => Clipboard(c, on: true, [])),
            new("clipboard-clear-asking", Panel(PanelSize.Clipboard), c => Clipboard(c, on: true, Rows, asking: true)),
            new("clipboard-targets", Panel(PanelSize.Clipboard), c => Clipboard(c, on: true, Rows, targets: true)),
            new("shortcuts-targets", Panel(PanelSize.Shortcuts), c => Shortcuts(c, targets: true)),
            new("palette-continuing", IslandGeometry.Panel(PanelSize.PaletteWidth, PaletteHeight("Is that this week?", Chips(false), Continuing)), c => Palette(c, "Is that this week?", Chips(false), "Nothing sent", Continuing, 0, 0.03m)),
            // Continue from History, then Ctrl+P: the back arrow before the prompt and the pinned mark on the tomte.
            new("palette-back-pinned", IslandGeometry.Panel(PanelSize.PaletteWidth, PaletteHeight("Is that this week?", Chips(false), Continuing)), c => Palette(c, "Is that this week?", Chips(false), "Nothing sent", Continuing, 0, 0.03m, back: true, pinned: true)),
            new("palette-ai-off-back", IslandGeometry.Panel(PanelSize.PaletteWidth, PanelSize.PaletteMinHeight), c => Palette(c, null, [], "", [], 0, 0, back: true)),
            .. DropZoneScenes(),
            .. HistoryScenes(),
            .. SearchScenes(),
            .. VoiceScenes(),
            CaptureHint(),
        ];
    }

    // The capture overlay's hint at the top of a monitor, as CaptureOverlay builds it: over the dim, the capsule and its line.
    private static Scene CaptureHint()
    {
        float text = TextSlot.Measure(LassoHint.Text, LassoHint.Style, 4096);
        float width = MathF.Ceiling(text) + 2 * LassoHint.Side + 80;
        return new("capture-hint", new IslandGeometry(width, 60, 0), c =>
        {
            var host = new Vector2(-width / 2, 0);
            var card = Offset(LassoHint.Card(width, text), host);
            c.RoundRect(new RectangleF(-width / 2 - 24, -24, width + 48, 108), 0, 0x59000000);
            c.RoundRect(card, card.Height / 2, 0xFF101012);
            c.StrokeRect(card, card.Height / 2, 0x1AFFFFFF);
            c.Text(LassoHint.Text, LassoHint.Style, LassoHint.Line(LassoHint.Card(width, text)), host);
        });
    }

    private sealed record Track(string Title, string Artist, double Position, double Length, bool Playing, string App = "Spotify", bool Timeline = true, bool Next = true);

    /// <summary>The pointer on the seek bar at <paramref name="X"/> (body DIPs), resting there or holding the knob in a drag.</summary>
    private sealed record Seeking(float X, bool Drag);

    // A target's area, drawn over a capture that shows where presses land.
    private const uint TargetLine = 0xFFFF00FF;

    private static readonly MediaPanelLayout MediaLayout = new((float)PanelSize.Media.W);

    // 60% of 1280 / 1.5 DIPs, as PanelSize.Fit gives it.
    private const double NarrowMedia = 512;

    // The Media panel's view: the same slots, styles and fallbacks as the island's. A button the app does not take is
    // drawn at the disabled opacity; the seek bar shows its knob and the time while the pointer is on it.
    private static void MediaPanel(Canvas c, Track? track, string? failure, Seeking? seek = null, bool targets = false, double width = PanelSize.Width)
    {
        var size = (W: width, PanelSize.Media.H);
        var layout = new MediaPanelLayout((float)width);
        var body = new Vector2((float)(-size.W / 2), PanelSpace.Head);
        c.Pill();
        c.Mascot();
        Tabs(c, PanelId.Media, size);
        var art = Offset(layout.Art, body);
        if (track is null)
        {
            c.RoundRect(art, MediaPanelLayout.ArtCorner, 0x1CFFFFFF);
        }
        else
        {
            c.Gradient(art, MediaPanelLayout.ArtCorner, 0xFF6A4A86, 0xFFC07A8E);
        }

        var line = layout.Bar;
        float played = (float)(track is { Timeline: true } ? track.Position / track.Length : 0);
        float fillEnd = seek is { Drag: true } ? Math.Clamp(seek.X, line.Left, line.Right) : line.X + line.Width * played;
        if (track is { Timeline: true })
        {
            var bar = Offset(line, body);
            c.RoundRect(bar, bar.Height / 2, 0x29FFFFFF);
            c.RoundRect(bar with { Width = fillEnd - line.X }, bar.Height / 2, 0xFFFFFFFF);
            if (seek is not null)
            {
                c.Circle(body + new Vector2(fillEnd, line.Y + line.Height / 2), MediaPanelLayout.Knob, 0xFFFFFFFF);
            }
        }

        uint lit = 0xFFFFFFFF, dim = (uint)(255 * StateLook.DisabledOpacity) << 24 | 0xFFFFFF;
        c.Glyph(Offset(layout.Previous, body), track is null ? dim : lit, [new(6, 4, 2, 16)], [[new(20, 4), new(9, 12), new(20, 20)]]);
        var button = Offset(layout.PlayPause, body);
        c.Circle(new Vector2(button.X + button.Width / 2, button.Y + button.Height / 2), MediaPanelLayout.Disc / 2, track is null ? dim : lit);
        if (track is { Playing: true })
        {
            c.Glyph(button, 0xFF000000, [new(6, 4, 4, 16), new(14, 4, 4, 16)], []);
        }
        else
        {
            c.Glyph(button, 0xFF000000, [], [[new(7, 4), new(21, 12), new(7, 20)]]);
        }

        c.Glyph(Offset(layout.Next, body), track is { Next: true } ? lit : dim, [new(16, 4, 2, 16)], [[new(4, 4), new(15, 12), new(4, 20)]]);

        c.Text(track is null ? NowPlaying.NothingPlaying : track.Title, MediaPanelLayout.TitleStyle, layout.Title, body);
        c.Text(failure ?? (track is null ? "" : track.Artist.Length > 0 ? track.Artist : track.App), failure is null ? MediaPanelLayout.ArtistStyle : MediaPanelLayout.FailedStyle, layout.Artist, body);
        if (track is { Timeline: true })
        {
            c.Text(NowPlaying.Clock(TimeSpan.FromSeconds(track.Position)), MediaPanelLayout.ElapsedStyle, layout.Elapsed, body);
            c.Text(NowPlaying.Clock(TimeSpan.FromSeconds(track.Length)), MediaPanelLayout.TotalStyle, layout.Total, body);
        }

        if (track is { Timeline: true } && seek is not null)
        {
            // The time's card sits over the artist's line, as the island inserts it last.
            c.Flush();
            var card = layout.SeekLabel(seek.X);
            c.RoundRect(Offset(card, body), 4, 0xFF2A2A2F);
            float at = Math.Clamp((seek.X - line.X) / line.Width, 0, 1);
            c.Text(NowPlaying.Clock(TimeSpan.FromSeconds(track.Length * at)), MediaPanelLayout.SeekStyle, card, body);
        }

        if (targets)
        {
            // Every target there can be: an app that takes every command and a position.
            var everything = NowPlaying.For("Spotify.exe")
                .WithPlayback(true, 1, MediaCommand.Play | MediaCommand.Pause | MediaCommand.Next | MediaCommand.Previous | MediaCommand.Seek, default)
                .WithTimeline(TimeSpan.Zero, TimeSpan.FromSeconds(195), TimeSpan.Zero, DateTimeOffset.Now, DateTimeOffset.Now);
            foreach (var target in layout.Targets(everything))
            {
                c.StrokeRect(Offset(target.Bounds, body), target.Corner, TargetLine);
            }
        }
    }

    // The Compact now-playing row: art, the title in its slot, four bars caught mid-breath. The peek pill and the pill
    // kept while music plays have no bars.
    private static void NowPlayingRow(Canvas c, string title, bool mascot = true, bool bars = true)
    {
        c.Pill();
        if (mascot)
        {
            c.Mascot();
        }

        var art = CompactLayout.Art(mascot);
        var slot = CompactLayout.Title(mascot);
        if (!bars)
        {
            // The art and its title as one group, as the row lays them out on a pill with no bars.
            var (lead, x, width) = CompactLayout.Group(mascot, art.Width, TextSlot.Measure(title, CompactLayout.TitleStyle, 4096));
            (art.X, slot.X, slot.Width) = (lead, x, width);
        }

        c.Gradient(art, CompactLayout.ArtCorner, 0xFF6A4A86, 0xFFC07A8E);
        c.Text(title, CompactLayout.TitleStyle, slot, Vector2.Zero);
        for (int i = 0; bars && i < CompactLayout.BarReach.Length; i++)
        {
            float h = Waveform.MinHeight + Waveform.Swing * CompactLayout.BarReach[i] * 0.7f;
            var bar = new RectangleF(CompactLayout.BarsLeft + i * (Waveform.BarWidth + Waveform.Gap), CompactLayout.BarsTop + (CompactLayout.BarsHeight - h) / 2, Waveform.BarWidth, h);
            c.RoundRect(bar, Waveform.BarWidth / 2, 0xFFFFFFFF);
        }
    }

    // 18:41 on the day the captures were made, in this PC's culture, as the push pill lays it out.
    private static readonly DateTimeOffset GlanceNow = new(DateTime.Today.AddHours(18).AddMinutes(41));

    private static string Glance(GlanceFacts facts, bool mascot = true) =>
        CompactRules.Glance(GlanceNow, facts, CompactLayout.Line(mascot).Width, line => TextSlot.Measure(line, CompactLayout.LineStyle, 4096));

    // The Compact line in its slot, or with the ring as one group when the ring shows.
    private static void CompactLine(Canvas c, string line, float? ring, bool tabular = false, bool mascot = true)
    {
        var pill = IslandGeometry.Compact;
        var style = tabular ? CompactLayout.ClockStyle : CompactLayout.LineStyle;
        c.Pill();
        if (mascot)
        {
            c.Mascot();
        }

        if (ring is { } fill)
        {
            var (left, x, width) = CompactLayout.Group(mascot, CompactLayout.RingSize, TextSlot.Measure(line, style, 4096));
            c.Ring(new Vector2(left + CompactLayout.RingSize / 2, (float)pill.Depth / 2), (CompactLayout.RingSize - CompactLayout.RingStroke) / 2, CompactLayout.RingStroke, fill);
            c.Text(line, style, new RectangleF(x, 0, width, (float)pill.Depth), Vector2.Zero);
            return;
        }

        c.Text(line, CompactLayout.Ending(style, led: mascot), CompactLayout.Line(mascot), Vector2.Zero);
    }

    // The voice key's pill while it records, as the rambler draws it: the microphone and seven bars mid-word, centred
    // in the room right of the tomte, or in the pill with the tomte off.
    private static void RamblerPill(Canvas c, bool mascot)
    {
        c.Pill();
        if (mascot)
        {
            c.Mascot();
        }

        const float group = 6 + 8 + (Waveform.Bars * Waveform.BarWidth) + ((Waveform.Bars - 1) * Waveform.Gap), height = Waveform.MinHeight + Waveform.Swing;
        float shift = mascot ? (MascotArt.InsetX + MascotArt.SizeDips) / 2 : 0, x = shift - (group / 2), y = (float)(IslandGeometry.Compact.Depth - height) / 2;
        c.RoundRect(new RectangleF(x, y + ((height - 11) / 2), 6, 11), 3, 0xFFFF6B6B);
        float[] reach = [.35f, .55f, .75f, .9f, 1f, .9f, .75f, .55f, .35f];
        for (int i = 0; i < Waveform.Bars; i++)
        {
            float h = Waveform.MinHeight + (Waveform.Swing * reach[i] * 0.7f);
            c.RoundRect(new RectangleF(x + 14 + (i * (Waveform.BarWidth + Waveform.Gap)), y + ((height - h) / 2), Waveform.BarWidth, h), Waveform.BarWidth / 2, 0xFFFFFFFF);
        }
    }

    // The pill after an AI view closed, as UndoPill lays it out: its line, then Undo, white, with Win+Alt+Z beside the
    // word unless another app holds the chord.
    private static Scene UndoPill(string name, string line, bool key, bool mascot = true)
    {
        var label = PaletteLayout.RunButton with { Align = TextAlign.Leading };
        var keyStyle = TextStyle.Small with { Color = 0x8C000000 };
        string chord = UndoClose.Chord.ToString();
        float lw = TextSlot.Measure("Undo", label, 200), kw = key ? TextSlot.Measure(chord, keyStyle, 200) : 0;
        var (width, slot, box) = CompactLayout.Undo(mascot, TextSlot.Measure(line, CompactLayout.LineStyle, 400), lw + (key ? CompactLayout.UndoKeyGap + kw : 0));
        return new(name, IslandGeometry.Compact with { Width = width }, c =>
        {
            c.Pill();
            if (mascot)
            {
                c.Mascot();
            }

            c.Text(line, CompactLayout.LineStyle, slot, Vector2.Zero);
            c.RoundRect(box, box.Height / 2, PaletteLayout.RunFill);
            float x = box.X + CompactLayout.UndoPadX;
            c.Text("Undo", label, new RectangleF(x, box.Y, lw + 1, box.Height), Vector2.Zero);
            if (key)
            {
                c.Text(chord, keyStyle, new RectangleF(x + lw + CompactLayout.UndoKeyGap, box.Y, kw + 1, box.Height), Vector2.Zero);
            }
        });
    }

    private static IslandGeometry Panel((double W, double H) size) => IslandGeometry.Panel(size.W, size.H);

    // The tab row as the panel host draws it: the rail, the current tab's capsule and name, and the two round buttons.
    // Simplification: the tab icons are left out here; the island draws them from the icon set.
    private static TabStrip.Row Tabs(Canvas c, PanelId current, (double W, double H) size)
    {
        var strip = new Vector2((float)(-size.W / 2), 0);
        var row = TabStrip.Layout(Enum.GetValues<PanelId>(), current, (float)size.W, mascot: true);
        c.RoundRect(Offset(row.Rail, strip), row.Rail.Height / 2, Theme.Card);
        foreach (var tab in row.Tabs)
        {
            if (tab.Label is { } label)
            {
                c.RoundRect(Offset(tab.Bounds, strip), tab.Bounds.Height / 2, Theme.RaisedHi);
                c.Text(label, strip);
            }
        }

        c.RoundRect(Offset(row.Search, strip), row.Search.Height / 2, Theme.Card);
        c.RoundRect(Offset(row.Settings, strip), row.Settings.Height / 2, Theme.Card);
        return row;
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
    // the Shortcuts tab (an acknowledgement with its tick). With search, the ring and the tooltip are the search button's.
    private static void StripLook(Canvas c, uint? layer = null, bool ring = false, string? tip = null, bool tick = false, bool search = false)
    {
        var size = PanelSize.Shelf;
        c.Pill();
        c.Mascot();
        var row = Tabs(c, PanelId.Shelf, size);
        var strip = new Vector2((float)(-size.W / 2), 0);
        var shortcuts = Offset(row.Tabs[2].Bounds, strip);
        var button = Offset(row.Search, strip);
        if (layer is { } fill)
        {
            c.RoundRect(shortcuts, TabStrip.TabHeight / 2, fill);
        }

        if (ring)
        {
            var (line, corner) = StateLook.FocusRing(search ? button : Offset(row.Tabs[1].Bounds, strip), TabStrip.TabHeight / 2);
            c.Stroke(line, corner, StateLook.Accent, StateLook.FocusWidth);
        }

        if (tip is not null)
        {
            var target = tick || !search ? shortcuts : button;
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

    private static Vector2 Body((double W, double H) size) => new((float)size.W, (float)size.H - PanelSpace.Head);

    private static Vector2 BodyOrigin((double W, double H) size) => new((float)(-size.W / 2), PanelSpace.Head);

    // The Shelf panel capped at 60% of a monitor 733 DIPs wide (1280 pixels at 175%), where the names no longer fit the band.
    private static readonly (double W, double H) NarrowShelf = (440, PanelSize.Shelf.H);

    private static readonly ShelfItem[] ShelfItems =
    [
        new(1, "Q3-report.pdf", @"C:\q\Q3-report.pdf", ShelfKind.Reference, default) { Size = 2_400_000 },
        new(2, "IMG_0412.jpg", @"C:\q\IMG_0412.jpg", ShelfKind.Reference, default) { Size = 3_100_000 },
        new(3, "Assets", @"C:\q\Assets", ShelfKind.Reference, default) { IsFolder = true },
        new(4, "design-notes.txt", @"C:\q\design-notes.txt", ShelfKind.Reference, default) { Missing = true },
        new(5, "a-long-name-for-the-invoice-of-october.docx", @"C:\q\invoice.docx", ShelfKind.Copy, default) { Size = 88_000 },
    ];

    // The Shelf: the band over a page of tiles, or the empty box. The band shows the head row, or the actions for the
    // tile in slot `owner`, the one under the pointer, with the host's hover layer on it and, unless it is the selected
    // one, the outline that says the buttons are its; Remove asks while `confirming`. `panel` can be narrower than the
    // Shelf's own size, as on a small monitor.
    private static void Shelf(Canvas c, ShelfItem[] items, (double W, double H) panel, int? owner = null, bool confirming = false, int? selectedSlot = null)
    {
        var size = Body(panel);
        var body = BodyOrigin(panel);
        c.Pill();
        c.Mascot();
        Tabs(c, PanelId.Shelf, panel);
        if (items.Length == 0)
        {
            var empty = new RectangleF(ShelfLayout.PadX, ShelfLayout.GridTop, size.X - 2 * ShelfLayout.PadX, size.Y - ShelfLayout.GridTop - ShelfLayout.PadBottom);
            c.StrokeRect(Offset(empty, body), 8, 0x2EFFFFFF);
            foreach (var line in ShelfLayout.EmptyText(empty.Size))
            {
                c.Text(line, body + new Vector2(empty.X, empty.Y));
            }
        }

        for (int slot = 0; slot < ShelfLayout.Visible(size, 0, items.Length); slot++)
        {
            var item = items[slot];
            var tile = ShelfLayout.Tile(slot, size);
            var origin = body + new Vector2(tile.X, tile.Y);
            var local = new RectangleF(0, 0, tile.Width, tile.Height);
            float dim = item.Missing ? 0.55f : 1;
            c.RoundRect(Offset(local, origin), ShelfLayout.TileCorner, slot == selectedSlot ? 0x1CFFFFFFu : 0x0FFFFFFFu, dim);
            c.RoundRect(Offset(ShelfLayout.PreviewIn(tile.Size), origin), ShelfLayout.PreviewCorner, ShelfLayout.PreviewOf(item) == ShelfPreview.Document ? Theme.Paper : Theme.Raised, dim);
            if (slot == owner)
            {
                c.RoundRect(Offset(local, origin), ShelfLayout.TileCorner, StateLook.Hover);
            }

            if (slot == selectedSlot)
            {
                c.StrokeRect(Offset(local, origin), ShelfLayout.TileCorner, 0xFF6FA8FF);
            }
            else if (slot == owner)
            {
                c.StrokeRect(Offset(local, origin), ShelfLayout.TileCorner, 0x4DFFFFFF);
            }

            foreach (var line in ShelfLayout.TileText(item, tile.Size))
            {
                c.Text(line, origin, dim);
            }
        }

        if (owner is not { } at)
        {
            foreach (var line in ShelfLayout.HeadText(size, items.Length))
            {
                c.Text(line, body);
            }

            return;
        }

        foreach (var slot in ShelfLayout.Actions(size, confirming))
        {
            bool asking = confirming && slot.Action == ShelfAction.Remove;
            bool allowed = ShelfStore.Allows(slot.Action, items[at]);
            c.RoundRect(Offset(slot.Box, body), ShelfLayout.ActionCorner, asking ? 0x29F0B35Au : 0x0FFFFFFFu);
            uint ink = asking ? 0xFFF0B35A : allowed ? 0xE6FFFFFF : 0x5CFFFFFF;
            c.Glyph(slot.Action, body + new Vector2(slot.Icon.X, slot.Icon.Y), ink);
            if (slot.Label is { } label)
            {
                c.Text(label with { Style = label.Style with { Color = ink } }, body);
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
    private static void Shortcuts(Canvas c, bool targets = false)
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
            var icon = ShortcutGrid.Icon(cell, c.Scale, PanelSpace.Head);
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

        if (targets)
        {
            var rows = tiles.Select((tile, i) => new Shortcut(i, tile.Name, tile.Name, null)).ToList();
            foreach (var target in ShortcutGrid.Targets(rows, 0, size).Where(t => t.Bounds.Height > 0))
            {
                c.StrokeRect(Offset(target.Bounds, body), target.Corner, TargetLine);
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

    private static readonly (string Name, ChipIcon Icon)[] ChipNames =
        [("Selection", ChipIcon.Selection), ("Window", ChipIcon.Window), ("Screenshot", ChipIcon.Screenshot), ("Shelf", ChipIcon.Shelf), ("Note", ChipIcon.Note)];

    // The hotkey palette's chips with the ones in <paramref name="on"/> turned on; with nothing selected the Selection
    // chip is not shown.
    private static ChipSpec[] Chips(bool selection, params ChipIcon[] on) =>
        [.. ChipNames.Skip(selection ? 0 : 1).Select(chip => new ChipSpec(chip.Name, chip.Icon, on.Contains(chip.Icon)))];

    // Circle-to-ask's: the region first and on, then the others.
    private static ChipSpec[] RegionChips() => [new("Region", ChipIcon.Region, true, Thumb: true), .. Chips(true)];

    // Right after a circle: the region, then the chips a plain window offers.
    private static readonly ChipSpec[] CircledChips = [new("Region", ChipIcon.Region, true, Thumb: true), new("Window", ChipIcon.Window, false), new("Screenshot", ChipIcon.Screenshot, false), new("Note", ChipIcon.Note, false)];

    // The line under the chips for those scenes, as the chips word it.
    private const string SomeOn = "Sends your question, the text selected in Notepad and a screenshot of Notepad";
    private const string RegionOn = "Sends your question and the region you circled";
    private const string SelectionOn = "Sends your question and the text selected in Notepad";

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
            // Read as a list, the items sit closer than paragraphs; these keep the answer long enough to scroll.
            "8. Run the System File Checker from an administrator terminal: sfc /scannow repairs system files an update needs.",
            "9. Then run DISM /Online /Cleanup-Image /RestoreHealth, which repairs the store that the System File Checker reads from.",
            "10. As a last step, an in-place upgrade from the Windows installation media keeps your files and apps and replaces Windows itself.",
        ]), PaletteLayout.AnswerText),
    ];

    // The model's Markdown, read as the drop result reads it.
    private static readonly (string, TextStyle)[] Marked =
    [
        ("Read budget.xlsx", PaletteLayout.StepText),
        ("## March\n\nYou spent **$3,720** in *March*, 12% more than in February. The sheet `Spending` has every line.\n\n- Rent: **$2,000**\n- Food: $940\n  - Eating out: $330\n\n1. Cancel the *unused* streaming plan.\n2. Shop once a week.", PaletteLayout.AnswerText),
    ];

    private static readonly (string, TextStyle)[] Asked = [("I can open it in your browser.", PaletteLayout.AnswerText)];

    private static readonly (string, TextStyle)[] Invoice = [("No. The line items add up to `$3,720`, but the total says `$3,820`: it's $100 over.", PaletteLayout.AnswerText)];

    private static readonly string[] AnswerActions = [PaletteLayout.CopyId, PaletteLayout.NoteId, PaletteLayout.ShelfId, PaletteLayout.FollowId];

    private static readonly Confirmation Asking = new("Open https://example.com/tomte/releases", Confirmation.LineFor(Tomte.Core.Device.ActionTier.ExternalEffect, ConfirmKeys.Prompt), AlwaysAllow: true, Destructive: false);

    private static readonly Confirmation PowerAsking = new("Shut down the PC", Confirmation.LineFor(Tomte.Core.Device.ActionTier.Destructive, ConfirmKeys.Voice), AlwaysAllow: false, Destructive: true);

    // The palette's height for a scene, from the prompt's own height (the text box's at 150%, or what was said by voice),
    // the chips, the question and the answer, as the view lays it out.
    private static double PaletteHeight(string prompt, ChipSpec[] chips, (string Text, TextStyle Style)[] answer, Confirmation? confirm = null, bool voice = false, string sends = "", IReadOnlyList<string>? actions = null)
    {
        var first = new PaletteLayout((float)PanelSize.PaletteWidth, mascot: true, send: !voice);
        float text;
        if (voice)
        {
            using var said = TextLayout.Of(new TextRun(prompt, PaletteLayout.PromptText, first.Editor.Width, PaletteLayout.PromptLines));
            text = said.Height;
        }
        else
        {
            using var probe = Typing.Captured(prompt, PaletteLayout.PromptSize, first.Editor, 1.5, null);
            text = (float)(probe.ContentHeight / 1.5);
        }

        var layout = new PaletteLayout((float)PanelSize.PaletteWidth, mascot: true, text, chips, confirm, send: !voice, sends, actions: actions);
        return layout.Height(layout.Answer(answer).Height);
    }

    // The palette: the prompt's card with its text box and Send (or the AI-off line), "Include" and the chips, the
    // answer and the footer, as the palette view lays them out. A null prompt is a palette with no provider. The answer
    // shows in its band, clipped there and scrolled to <paramref name="scrolled"/> of its range (1 is the end, where a
    // streaming answer stays). In a typed palette the box has the keys. While <paramref name="sending"/> the chips and
    // Send are dimmed; <paramref name="run"/> is Run's opacity, dim while its read guard runs. Opened from the panels it
    // has the back arrow (<paramref name="back"/>); <paramref name="pinned"/> puts the pinned mark on the tomte.
    private static void Palette(Canvas c, string? prompt, ChipSpec[] chips, string provider, (string Text, TextStyle Style)[] answer, int steps, decimal spent, float scrolled = 1, Confirmation? confirm = null, bool voice = false, bool sending = false, float run = 1, string sends = "", bool back = false, bool pinned = false, IReadOnlyList<string>? actions = null, decimal cost = 0)
    {
        var origin = new Vector2((float)(-PanelSize.PaletteWidth / 2), 0);
        c.Pill();
        c.Mascot(pinned, PaletteLayout.Seat.Shift);
        float text = 0;
        TextServices? editor = null;
        bool typed = prompt is not null && !voice;
        var first = new PaletteLayout((float)PanelSize.PaletteWidth, mascot: true, send: typed);
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
        var layout = new PaletteLayout((float)PanelSize.PaletteWidth, mascot: true, text, chips, confirm, send: typed, sends, back, actions);
        if (back)
        {
            var arrow = Offset(layout.Back, origin);
            c.Figures(PaletteGlyphs.Back, new Vector2(arrow.X + ((arrow.Width - PaletteGlyphs.SendSize) / 2), arrow.Y + ((arrow.Height - PaletteGlyphs.SendSize) / 2)), Theme.Ink2);
        }

        var card = layout.Prompt;
        c.RoundRect(Offset(card, origin), PaletteLayout.PromptCorner, Theme.Card);
        var box = layout.Editor;
        if (prompt is null)
        {
            c.Text(PaletteLayout.Off, PaletteLayout.Placeholder, layout.HintLine, origin);
            c.Text(new TextRun(PaletteLayout.Setup, TextStyle.Secondary, layout.AnswerWidth), new Vector2(PaletteLayout.Pad + PaletteLayout.AnswerInset, layout.AnswerTop), origin);
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
            if (prompt.Length == 0)
            {
                c.Text(PaletteLayout.Hint, PaletteLayout.Placeholder, layout.HintLine, origin);
            }

            // Send, in a visual of its own that dims as one.
            var send = Offset(layout.Send, origin);
            c.Layer(PaletteLayout.CanSend(prompt, sending) ? 1 : StateLook.DisabledOpacity);
            c.RoundRect(send, PaletteLayout.SendCorner, PaletteLayout.SendFill);
            c.Glyph(Glyphs.Send, new Vector2(send.X + (send.Width - PaletteLayout.SendGlyph) / 2, send.Y + (send.Height - PaletteLayout.SendGlyph) / 2), PaletteLayout.SendGlyph, PaletteLayout.SendInk);
            c.EndLayer();
        }

        // The chips' shapes in one layer and their names on top, all dimmed while sending, as the view's chip layer is.
        float dim = sending ? StateLook.DisabledOpacity : 1;
        c.Text(layout.SendsLine, origin, dim);
        c.Layer(dim);
        for (int i = 0; i < layout.Chips.Count; i++)
        {
            var chip = layout.Chips[i];
            // The view fills and strokes one path, a pixel-wide stroke inside the pill's edges on whole pixels.
            var (path, corner, stroke) = PaletteLayout.ChipStroke(chip, origin, c.Scale);
            if (chip.On)
            {
                c.RoundRect(Offset(path, origin), corner, PaletteLayout.ChipOnFill);
            }
            else
            {
                c.Stroke(Offset(path, origin), corner, PaletteLayout.ChipOutline, stroke);
            }

            uint ink = chip.On ? PaletteLayout.ChipOnInk : PaletteLayout.ChipInk;
            var icon = Offset(chip.Icon, origin);
            var mark = Offset(chip.Mark, origin);
            if (chip.Thumb)
            {
                // The circled region as the view shows it: covered into the slot's pixels, its corners rounded.
                int w = (int)Math.Round(icon.Width * c.Scale), h = (int)Math.Round(icon.Height * c.Scale);
                c.Picture(icon, PaletteLayout.ThumbCorner, ClipboardImages.Thumbnail(ImageRows.Value[0].Thumb!, w, h), w, h);
            }
            else
            {
                c.Glyph(ChipGlyph(chips[i].Icon), new Vector2(icon.X, icon.Y), PaletteLayout.IconSize, ink);
            }

            c.Glyph(chip.On ? Glyphs.Close : Glyphs.Plus, new Vector2(mark.X, mark.Y), PaletteLayout.MarkSize, ink);
        }

        c.EndLayer();
        foreach (var chip in layout.Chips)
        {
            c.Text(chip.Label, origin, dim);
        }

        var (runs, measured) = layout.Answer(answer);
        float height = layout.Height(measured);
        var band = layout.AnswerBand(height);
        float scroll = layout.MaxScroll(measured, height) * scrolled;
        c.Clip = Offset(band, origin);
        foreach (var (line, at) in runs)
        {
            c.Text(line, new Vector2(at.X, band.Y + at.Y - scroll), origin);
        }

        c.Clip = null;
        if (runs.Count > 0 && layout.Sparkle.Y + band.Y - scroll >= band.Y)
        {
            var spark = layout.Sparkle;
            c.Glyph(Glyphs.Sparkle, new Vector2(spark.X, band.Y + spark.Y - scroll) + origin, PaletteLayout.SparkleSize, Theme.Accent);
        }

        foreach (var action in layout.Actions(height))
        {
            var rect = Offset(action.Box, origin);
            if (action.Outlined)
            {
                c.Stroke(new RectangleF(rect.X + 0.5f, rect.Y + 0.5f, rect.Width - 1, rect.Height - 1), rect.Height / 2, PaletteLayout.ActionOutline, 1);
            }
            else
            {
                c.RoundRect(rect, rect.Height / 2, PaletteLayout.ActionFill);
            }

            c.Text(action.Label, origin);
        }

        // The question, with the view's colours: the accent round the card, Run white or amber for what asks every time,
        // the others filled and outlined; each button in a layer of its own, the yes buttons at their read guard's opacity.
        if (layout.Ask(height) is { } ask)
        {
            c.RoundRect(Offset(ask.Card, origin), PaletteLayout.PromptCorner, Theme.Card);
            c.Stroke(Offset(ask.Card, origin), PaletteLayout.PromptCorner, PaletteLayout.AskOutline, 1);
            c.Text(ask.Summary, ask.SummaryAt, origin);
            c.Text(ask.Line, origin);
            foreach (var (button, at, label) in ask.Buttons)
            {
                var rect = Offset(at, origin);
                float opacity = button == ConfirmButton.Cancel ? 1 : run;
                c.Layer(opacity);
                if (button == ConfirmButton.Run)
                {
                    c.RoundRect(rect, PaletteLayout.ButtonCorner, confirm!.Destructive ? PaletteLayout.RunWarn : PaletteLayout.RunFill);
                }
                else
                {
                    c.RoundRect(rect, PaletteLayout.ButtonCorner, PaletteLayout.ButtonFill);
                    c.Stroke(rect, PaletteLayout.ButtonCorner, PaletteLayout.ButtonOutline, 1);
                }

                c.EndLayer();
                c.Text(label, origin, opacity);
            }
        }

        bool history = !voice && prompt is not null;
        var foot = layout.Foot(provider, steps, cost, spent, 2m, height, history);
        c.RoundRect(Offset(foot.Divider, origin), 0, PaletteLayout.Divider);
        c.Circle(new Vector2(foot.Dot.X + (foot.Dot.Width / 2), foot.Dot.Y + (foot.Dot.Height / 2)) + origin, foot.Dot.Width / 2, PaletteLayout.StatusDot(provider, failed: false));
        c.Text(foot.Provider, origin);
        c.Text(foot.Cost, origin);
        c.Text(foot.Spend, origin);
        c.RoundRect(Offset(foot.Meter, origin), PaletteLayout.MeterHeight / 2, PaletteLayout.MeterTrack);
        c.RoundRect(Offset(foot.Meter with { Width = foot.Fill }, origin), PaletteLayout.MeterHeight / 2, PaletteLayout.MeterFill);

        // The AI history button's clock, as the view draws it; the voice palette has none.
        if (history)
        {
            var button = Offset(layout.History(height), origin);
            c.StrokeCircle(new Vector2(button.X + button.Width / 2, button.Y + button.Height / 2), PaletteLayout.ClockRadius, PaletteLayout.ClockStroke, Theme.Ink2);
            foreach (var hand in PaletteLayout.ClockHands)
            {
                c.RoundRect(Offset(hand, new Vector2(button.X, button.Y)), 0.75f, Theme.Ink2);
            }
        }
    }

    // A chip's picture from the icon set, as the view picks it.
    private static GlyphFigure[] ChipGlyph(ChipIcon icon) => icon switch
    {
        ChipIcon.Selection => Glyphs.Text,
        ChipIcon.Window => Glyphs.Window,
        ChipIcon.Screenshot => Glyphs.Region,
        ChipIcon.Shelf => Glyphs.Shelf,
        ChipIcon.Note => Glyphs.Pencil,
        _ => Glyphs.Region,
    };

    // A conversation from the History tab, loaded and not sent yet: the box says what going on with it sends.
    private static readonly (string, TextStyle)[] Continuing = [(PaletteLayout.ContinueLine("Remind me what the dentist said"), PaletteLayout.StepText)];

    private static readonly (string Display, string Ago, byte[]? Thumb)[] Rows =
    [
        ("Pull up last quarter's numbers, compare them with this one", "2 min", null),
        ("learn.microsoft.com/windows/win32/api/dwmapi", "9 min", null),
        ("WS_EX_NOREDIRECTIONBITMAP | WS_EX_TOPMOST | WS_EX_TOOLWINDOW", "31 min", null),
        ("Meet at 10, not 9", "1 h", null),
        ("Screenshot of the shelf, 1280 x 720", "3 d", null),
    ];

    // Image rows made by history's own path from generated bitmaps: a screenshot, a portrait photo and an icon.
    private static readonly Lazy<(string Display, string Ago, byte[]? Thumb)[]> ImageRows = new(() =>
    {
        var screenshot = Kept(1920, 1080, (x, y) =>
            x < 160 || x >= 1760 || y < 90 || y >= 990 ? (0x50, 0x3A, 0x20)
            : y < 130 ? (0xFF, 0xFF, 0xFF)
            : x < 460 ? (0xEE, 0xE6, 0xE0)
            : x is < 520 or >= 1700 ? (0xF3, 0xF3, 0xF3)
            : y < 330 ? (0xD8, 0x9A, 0x3C)
            : y > 380 && (y - 380) % 120 < 80 ? (0xFF, 0xFF, 0xFF)
            : (0xF3, 0xF3, 0xF3));
        var photo = Kept(1080, 1350, (x, y) => y > 900 ? (0x20, 0x30, 0x24) : (x - 700) * (x - 700) + (y - 520) * (y - 520) < 14400 ? (0x60, 0xD0, 0xFF) : (200 - y / 6, 120 + y / 10, 40 + y / 5));
        var icon = Kept(64, 64, (x, y) => (x - 32) * (x - 32) + (y - 32) * (y - 32) < 400 ? (0xFF, 0xFF, 0xFF) : (0x88, 0x80, 0x10));
        return
        [
            (ClipboardRow.ImageLabel(screenshot.Width, screenshot.Height), "now", screenshot.Thumb),
            ("Meet at 10, not 9", "4 min", null),
            (ClipboardRow.ImageLabel(photo.Width, photo.Height), "12 min", photo.Thumb),
            ("learn.microsoft.com/windows/win32/api/dwmapi", "31 min", null),
            (ClipboardRow.ImageLabel(icon.Width, icon.Height), "2 h", icon.Thumb),
        ];
    });

    // A generated bitmap as an app puts it on the clipboard (24 bits, bottom row first), kept as history keeps it.
    private static KeptImage Kept(int width, int height, Func<int, int, (int B, int G, int R)> colour)
    {
        int stride = (width * 3 + 3) & ~3;
        var dib = new byte[40 + stride * height];
        BitConverter.TryWriteBytes(dib.AsSpan(0), 40);
        BitConverter.TryWriteBytes(dib.AsSpan(4), width);
        BitConverter.TryWriteBytes(dib.AsSpan(8), height);
        BitConverter.TryWriteBytes(dib.AsSpan(12), (short)1);
        BitConverter.TryWriteBytes(dib.AsSpan(14), (short)24);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int at = 40 + (height - 1 - y) * stride + x * 3;
                var (b, g, r) = colour(x, y);
                (dib[at], dib[at + 1], dib[at + 2]) = ((byte)b, (byte)g, (byte)r);
            }
        }

        using var copied = new CopiedImage(dib, png: false);
        return ClipboardImages.Keep(copied, new ClipboardRetention().MaxImageReadBytes)!;
    }

    // The Clipboard history: on with rows, on and empty, or off; Clear all at rest or asking; optionally where presses land.
    private static void Clipboard(Canvas c, bool on, (string Display, string Ago, byte[]? Thumb)[] rows, bool asking = false, bool targets = false)
    {
        var layout = new ClipboardPanelLayout(Body(PanelSize.Clipboard));
        var body = BodyOrigin(PanelSize.Clipboard);
        var now = new DateTimeOffset(2026, 10, 20, 15, 0, 0, TimeSpan.Zero);
        var kept = new ClipboardRetention();
        c.Pill();
        c.Mascot();
        Tabs(c, PanelId.Clipboard, PanelSize.Clipboard);
        c.Text(new TextSlot(ClipboardPanelLayout.KeepHistory, TextStyle.Secondary, RectangleF.FromLTRB(ClipboardPanelLayout.Pad, layout.Head.Y, layout.Switch.X, layout.Head.Bottom)), body);
        var track = Offset(layout.Switch, body);
        c.RoundRect(track, track.Height / 2, on ? Theme.Accent : Theme.LineStrong);
        c.Circle(new Vector2(on ? track.Right - 10 : track.X + 10, track.Y + track.Height / 2), 7, Theme.Ink);
        if (on)
        {
            c.RoundRect(Offset(layout.Filter, body), layout.Filter.Height / 2, Theme.Card);
            c.Text(new TextSlot(ClipboardPanelLayout.FilterHint, ClipboardPanelLayout.HintStyle, layout.FilterBox), body);
        }

        if (!on || rows.Length == 0)
        {
            c.StrokeRect(Offset(layout.Box, body), ClipboardPanelLayout.PreviewCorner, Theme.LineStrong);
            foreach (var line in layout.BoxText(on, kept))
            {
                c.Text(line, body);
            }

            return;
        }

        foreach (var line in layout.FootText(asking, kept))
        {
            c.Text(line, body);
        }

        // The rows a few minutes apart from now, the first one selected.
        var history = rows.Select((r, i) => new ClipboardRow(i + 1, r.Display, now.AddMinutes(-7 * i), r.Thumb) { Chars = r.Display.Length }).ToList();
        var lines = ClipboardPanelLayout.Lines(history, now);
        var list = layout.List;
        foreach (var line in lines)
        {
            if (line.Y + line.Height > list.Height)
            {
                break;
            }

            var origin = body + new Vector2(list.X, list.Y + line.Y);
            if (line.IsHeading)
            {
                c.Text(ClipboardPanelLayout.HeadingSlot(line, list.Width), body + new Vector2(list.X, list.Y));
                continue;
            }

            var row = history[line.Row];
            bool selected = line.Row == 0;
            if (selected)
            {
                c.RoundRect(Offset(new RectangleF(0, 0, list.Width, line.Height), origin), ClipboardPanelLayout.Corner, Theme.Raised);
                c.StrokeRect(Offset(new RectangleF(0, 0, list.Width, line.Height), origin), ClipboardPanelLayout.Corner, Theme.Dash);
            }

            if (row.Thumb is { } thumb)
            {
                // Decoded to the slot's pixels at this scale, as the panel does when it shows.
                int w = (int)Math.Round(ClipboardPanelLayout.ThumbWidth * c.Scale), h = (int)Math.Round(ClipboardPanelLayout.ThumbHeight * c.Scale);
                c.Picture(Offset(ClipboardPanelLayout.ThumbIn, origin), ClipboardPanelLayout.ThumbCorner, ClipboardImages.Thumbnail(thumb, w, h), w, h);
            }
            else
            {
                c.RoundRect(Offset(ClipboardPanelLayout.IconIn, origin), 4, Theme.RaisedHi);
            }

            var end = selected ? ClipboardPanelLayout.RowEnd.Enter : ClipboardPanelLayout.RowEnd.Age;
            foreach (var slot in ClipboardPanelLayout.RowText(row, row.When(now), end, list.Width))
            {
                c.Text(slot, origin);
            }
        }

        if (layout.HasPreview)
        {
            c.RoundRect(Offset(layout.Preview, body), ClipboardPanelLayout.PreviewCorner, Theme.Card);
            c.Text(layout.PreviewMeta(history[0].Meta()), body);
            foreach (var (action, box) in layout.Actions())
            {
                c.RoundRect(Offset(box, body), ClipboardPanelLayout.ButtonCorner, action == ClipboardPanelLayout.PreviewAction.Paste ? Theme.Ink : Theme.Raised);
                c.Text(new TextSlot(ClipboardPanelLayout.ActionText(action, false), action == ClipboardPanelLayout.PreviewAction.Paste ? ClipboardPanelLayout.PasteStyle : ClipboardPanelLayout.SecondaryButtonStyle, box), body);
            }
        }

        if (targets)
        {
            foreach (var target in layout.Targets(on, history, lines, 0, asking, history[0]).Where(t => t.Bounds.Height > 0))
            {
                c.StrokeRect(Offset(target.Bounds, body), target.Corner, TargetLine);
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

        /// <summary>
        /// The tomte in its box at the pill's left edge, moved by <paramref name="shift"/> toward the seat beside the
        /// content (<see cref="MascotSeat.Shift"/>), at the size and opacity the shape's depth gives it.
        /// </summary>
        public void Mascot(bool pinned = false, Vector2 shift = default)
        {
            var box = MascotArt.Box(g, shift);
            var at = new Vector2(box.X, box.Y);
            float k = box.Width / MascotArt.ViewBox, ride = (float)MascotArt.Ride(g);
            if (ride < 1)
            {
                Layer(ride);
            }
            uint cap = 0xFF000000 | MascotArt.CapRed, shade = 0xFF000000 | MascotArt.Shade(MascotArt.CapRed);
            Fill(MascotArt.Beard, at, k, 0xFF000000 | MascotArt.BeardColor);
            Fill(MascotArt.BeardShade, at, k, 0xFF000000 | MascotArt.BeardShadeColor);
            Circle(at + MascotArt.Nose.Center * k, MascotArt.Nose.Radius * k, 0xFF000000 | MascotArt.NoseColor);
            Fill(MascotArt.CapCone, at, k, cap);
            Fill(MascotArt.TipCurl, at, k, cap);
            Circle(at + MascotArt.TipBulb.Center * k, MascotArt.TipBulb.Radius * k, cap);
            Fill(MascotArt.Brim, at, k, shade);
            if (pinned)
            {
                // The pinned mark, as the mascot visual draws it over the tomte.
                Circle(at + MascotArt.PinRing.Center * k, MascotArt.PinRing.Radius * k, 0xFF000000);
                Circle(at + MascotArt.PinDisc.Center * k, MascotArt.PinDisc.Radius * k, StateLook.Accent);
                Figures(MascotArt.Pin, at, k, 0xFF000000);
            }

            if (ride < 1)
            {
                EndLayer();
            }
        }

        public void RoundRect(RectangleF r, float radius, uint argb, float opacity = 1)
        {
            var brush = Brush(argb);
            brush->SetOpacity(opacity);
            var round = new D2D1_ROUNDED_RECT { rect = Rect(r), radiusX = radius, radiusY = radius };
            dc->FillRoundedRectangle(&round, (ID2D1Brush*)brush);
            brush->Release();
        }

        /// <summary>Premultiplied BGRA pixels, one per device pixel, filling the rectangle with rounded corners.</summary>
        public void Picture(RectangleF r, float radius, byte[] pixels, int w, int h)
        {
            var props = new D2D1_BITMAP_PROPERTIES1 { pixelFormat = Format, dpiX = (float)(96 * scale), dpiY = (float)(96 * scale) };
            ID2D1Bitmap1* bitmap;
            fixed (byte* bits = pixels)
            {
                dc->CreateBitmap(new D2D_SIZE_U { width = (uint)w, height = (uint)h }, bits, (uint)w * 4, &props, &bitmap);
            }

            ID2D1BitmapBrush1* brush;
            dc->CreateBitmapBrush((ID2D1Bitmap*)bitmap, (D2D1_BITMAP_BRUSH_PROPERTIES1*)null, (D2D1_BRUSH_PROPERTIES*)null, &brush);
            var move = new D2D_MATRIX_3X2_F { m11 = 1, m22 = 1, dx = r.X, dy = r.Y };
            brush->SetTransform(&move);
            var round = new D2D1_ROUNDED_RECT { rect = Rect(r), radiusX = radius, radiusY = radius };
            dc->FillRoundedRectangle(&round, (ID2D1Brush*)brush);
            brush->Release();
            bitmap->Release();
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

        /// <summary>A circle's outline.</summary>
        public void StrokeCircle(Vector2 centre, float radius, float stroke, uint argb)
        {
            var brush = Brush(argb);
            var ellipse = new D2D1_ELLIPSE { point = new D2D_POINT_2F { x = centre.X, y = centre.Y }, radiusX = radius, radiusY = radius };
            dc->DrawEllipse(&ellipse, (ID2D1Brush*)brush, stroke, null);
            brush->Release();
        }

        /// <summary>Closed figures, one each, scaled by <paramref name="k"/> from <paramref name="at"/>.</summary>
        public void Figures(OutlineSegment[][] figures, Vector2 at, float k, uint argb)
        {
            foreach (var figure in figures)
            {
                Fill(figure, at, k, argb);
            }
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

        /// <summary>An x of two strokes, <paramref name="size"/> DIPs long, crossing at <paramref name="centre"/>.</summary>
        public void Cross(Vector2 centre, float size, float width, uint argb)
        {
            var brush = Brush(argb);
            float half = size / 2 / MathF.Sqrt(2);
            dc->DrawLine(new D2D_POINT_2F { x = centre.X - half, y = centre.Y - half }, new D2D_POINT_2F { x = centre.X + half, y = centre.Y + half }, (ID2D1Brush*)brush, width, null);
            dc->DrawLine(new D2D_POINT_2F { x = centre.X - half, y = centre.Y + half }, new D2D_POINT_2F { x = centre.X + half, y = centre.Y - half }, (ID2D1Brush*)brush, width, null);
            brush->Release();
        }

        /// <summary>A palette glyph's figures from their box's top-left corner: filled, or stroked along their outlines.</summary>
        public void Figures(GlyphFigure[] figures, Vector2 at, uint argb)
        {
            foreach (var figure in figures)
            {
                if (!figure.Stroked)
                {
                    Fill(figure.Path, at, 1, argb);
                    continue;
                }

                var path = Path(figure.Path, at, 1);
                var brush = Brush(argb);
                dc->DrawGeometry((ID2D1Geometry*)path, (ID2D1Brush*)brush, PaletteGlyphs.Stroke, null);
                brush->Release();
                path->Release();
            }
        }

        /// <summary>A <see cref="Glyphs"/> entry from its 24-unit box, <paramref name="size"/> DIPs square from <paramref name="at"/>.</summary>
        public void Glyph(GlyphFigure[] figures, Vector2 at, float size, uint argb)
        {
            float k = size / Glyphs.Box;
            foreach (var figure in figures)
            {
                if (!figure.Stroked)
                {
                    Fill(figure.Path, at, k, argb);
                    continue;
                }

                var path = Path(figure.Path, at, k);
                var brush = Brush(argb);
                dc->DrawGeometry((ID2D1Geometry*)path, (ID2D1Brush*)brush, Glyphs.StrokeWidth * k, null);
                brush->Release();
                path->Release();
            }
        }

        /// <summary>A run with its parts (bold, tinted letters) in a slot of a host, centred in its height.</summary>
        public void Text(TextRun run, RectangleF slot, Vector2 host) => Text(run, slot, host, centred: true, 1);

        /// <summary>What is drawn from here to <see cref="EndLayer"/> fades as one, as the shapes of one shape visual do.</summary>
        public void Layer(float opacity)
        {
            var parameters = new D2D1_LAYER_PARAMETERS1
            {
                contentBounds = new D2D_RECT_F { left = float.MinValue, top = float.MinValue, right = float.MaxValue, bottom = float.MaxValue },
                maskAntialiasMode = D2D1_ANTIALIAS_MODE.D2D1_ANTIALIAS_MODE_PER_PRIMITIVE,
                maskTransform = new D2D_MATRIX_3X2_F { m11 = 1, m22 = 1 },
                opacity = opacity,
            };
            dc->PushLayer(&parameters, null);
        }

        public void EndLayer() => dc->PopLayer();

        /// <summary>A rounded outline one pixel wide, inside the rectangle.</summary>
        public void StrokeRect(RectangleF r, float radius, uint argb)
        {
            var brush = Brush(argb);
            float half = (float)(0.5 / scale);
            var round = new D2D1_ROUNDED_RECT { rect = Rect(new RectangleF(r.X + half, r.Y + half, r.Width - 2 * half, r.Height - 2 * half)), radiusX = radius, radiusY = radius };
            dc->DrawRoundedRectangle(&round, (ID2D1Brush*)brush, (float)(1 / scale), null);
            brush->Release();
        }

        /// <summary>A shelf action's line icon at the top-left corner of its square, as the island's sprite shapes draw it.</summary>
        public void Glyph(ShelfAction action, Vector2 at, uint argb)
        {
            var brush = Brush(argb);
            var props = new D2D1_STROKE_STYLE_PROPERTIES { startCap = D2D1_CAP_STYLE.D2D1_CAP_STYLE_ROUND, endCap = D2D1_CAP_STYLE.D2D1_CAP_STYLE_ROUND };
            ID2D1StrokeStyle* round;
            factory->CreateStrokeStyle(&props, null, 0, &round);
            const float w = ShelfLayout.GlyphStrokeWidth, inset = w / 2;
            foreach (var s in ShelfLayout.Glyph(action))
            {
                if (s.Corner < 0)
                {
                    dc->DrawLine(new D2D_POINT_2F { x = at.X + s.A.X, y = at.Y + s.A.Y }, new D2D_POINT_2F { x = at.X + s.B.X, y = at.Y + s.B.Y }, (ID2D1Brush*)brush, w, round);
                }
                else
                {
                    // As the island's box: the stroke kept inside the rectangle.
                    var r = new RectangleF(at.X + s.A.X + inset, at.Y + s.A.Y + inset, s.B.X - s.A.X - w, s.B.Y - s.A.Y - w);
                    var box = new D2D1_ROUNDED_RECT { rect = Rect(r), radiusX = s.Corner, radiusY = s.Corner };
                    dc->DrawRoundedRectangle(&box, (ID2D1Brush*)brush, w, null);
                }
            }

            round->Release();
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
            Flush();
            dc->EndDraw(null, null);
            Png(file, Read(dc, target, width, height, scale), width, height);
        }

        /// <summary>
        /// The runs so far go onto the canvas now, as the island's text sprites sit on top in their hosts; shapes drawn
        /// after this go over them, as a visual inserted later does.
        /// </summary>
        public void Flush()
        {
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

            runs.Clear();
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

        internal static void Png(string file, byte[] pixels, int width, int height)
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
