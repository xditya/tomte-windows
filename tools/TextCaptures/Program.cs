using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using Tomte.Core;
using Tomte.Core.Media;
using Tomte.Core.Rambler;
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
internal static unsafe class Program
{
    private static readonly double[] Scales = [1, 1.25, 1.5];
    private static readonly List<double> LayoutMs = [], DrawMs = [];
    private static int OffGrid;

    private static int Main(string[] args)
    {
        string dir = args.Length > 0 ? args[0] : "captures";
        Directory.CreateDirectory(dir);
        var report = new List<string> { $"machine {Environment.MachineName}, {Environment.OSVersion}" };
        report.Add(Memory("start"));

        var watch = Stopwatch.StartNew();
        using (var first = TextLayout.Of(new TextRun("Nightfall", new TextStyle(14, 600), 272)))
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
        using var layout = TextLayout.Of(new TextRun("A very long title that will not fit in the slot at all, not even close, and goes on for a while", new TextStyle(14, 600), 120, 2));
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

        var titleStyle = new TextStyle(14, 600);
        var artistStyle = new TextStyle(12, Color: TextStyle.Secondary);
        var failedStyle = new TextStyle(12, Color: TextStyle.Warn);
        var elapsedStyle = new TextStyle(11, Color: TextStyle.Tertiary, Tabular: true);
        c.Text(track is null ? NowPlaying.NothingPlaying : track.Title, titleStyle, MediaPanelLayout.Title, body);
        c.Text(failure ?? (track is null ? "" : track.Artist.Length > 0 ? track.Artist : track.App), failure is null ? artistStyle : failedStyle, MediaPanelLayout.Artist, body);
        if (track is { Timeline: true })
        {
            c.Text(NowPlaying.Clock(TimeSpan.FromSeconds(track.Position)), elapsedStyle, MediaPanelLayout.Elapsed, body);
            c.Text(NowPlaying.Clock(TimeSpan.FromSeconds(track.Length)), elapsedStyle with { Align = TextAlign.Trailing }, MediaPanelLayout.Total, body);
        }
    }

    // The Compact now-playing row: art, the title in its slot, four bars caught mid-breath.
    private static void NowPlayingRow(Canvas c, string title)
    {
        c.Pill();
        c.Mascot();
        c.Gradient(new RectangleF(-67, 8, 22, 22), 6, 0xFF6A4A86, 0xFFC07A8E);
        c.Text(title, new TextStyle(13, 600), new RectangleF(-36, 9, 107, 20), Vector2.Zero);
        float[] reach = [.35f, .62f, .85f, 1f];
        for (int i = 0; i < reach.Length; i++)
        {
            float h = Waveform.MinHeight + Waveform.Swing * reach[i] * 0.7f;
            c.RoundRect(new RectangleF(80 + i * (Waveform.BarWidth + Waveform.Gap), 9 + (20 - h) / 2, Waveform.BarWidth, h), Waveform.BarWidth / 2, 0xFFFFFFFF);
        }
    }

    // The Compact line: after the mascot (8 + 26 + 9), or after the ring (18 + 9 more) when the ring shows.
    private static void CompactLine(Canvas c, string line, float? ring, bool tabular = false)
    {
        var pill = IslandGeometry.Compact;
        float left = (float)(-pill.Width / 2) + 43;
        c.Pill();
        c.Mascot();
        if (ring is { } fill)
        {
            c.Ring(new Vector2(left + 9, (float)pill.Depth / 2), (18 - 3.5f) / 2, 3.5f, fill);
            left += 18 + 9;
        }

        c.Text(line, new TextStyle(13, 600, Tabular: tabular), new RectangleF(left, 0, (float)(pill.Width / 2) - 12 - left, (float)pill.Depth), Vector2.Zero);
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
        private readonly List<(byte[] Pixels, int Width, int Height, Vector2 At)> runs = [];

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

        /// <summary>The tomte at rest, 26 DIPs for its 64-unit art, 8 in from the pill's left and 3.5 down.</summary>
        public void Mascot()
        {
            var at = new Vector2((float)(-g.Width / 2) + 8, 3.5f);
            float k = 26 / MascotArt.ViewBox;
            uint cap = 0xFF000000 | MascotArt.CapRed, shade = 0xFF000000 | MascotArt.Shade(MascotArt.CapRed);
            Fill(MascotArt.Beard, at, k, 0xFF000000 | MascotArt.BeardColor);
            Fill(MascotArt.BeardShade, at, k, 0xFF000000 | MascotArt.BeardShadeColor);
            Circle(at + MascotArt.Nose.Center * k, MascotArt.Nose.Radius * k, 0xFF000000 | MascotArt.NoseColor);
            Fill(MascotArt.CapCone, at, k, cap);
            Fill(MascotArt.TipCurl, at, k, cap);
            Circle(at + MascotArt.TipBulb.Center * k, MascotArt.TipBulb.Radius * k, cap);
            Fill(MascotArt.Brim, at, k, shade);
        }

        public void RoundRect(RectangleF r, float radius, uint argb)
        {
            var brush = Brush(argb);
            var round = new D2D1_ROUNDED_RECT { rect = Rect(r), radiusX = radius, radiusY = radius };
            dc->FillRoundedRectangle(&round, (ID2D1Brush*)brush);
            brush->Release();
        }

        /// <summary>The design's stand-in art: a diagonal gradient.</summary>
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

        /// <summary>
        /// One line in a slot of a host, as the island's text visual places it: as wide as the slot, centred in its
        /// height, snapped to the pixel grid, on a surface the text's size plus the pad.
        /// </summary>
        public void Text(string text, TextStyle style, RectangleF slot, Vector2 host)
        {
            var watch = Stopwatch.StartNew();
            using var block = new TextBlock();
            block.Set(new TextRun(text, style, slot.Width), scale);
            LayoutMs.Add(watch.Elapsed.TotalMilliseconds);
            if (block.Layout is not { } layout)
            {
                return;
            }

            float x = PixelGrid.Snap(host.X, slot.X, scale), y = PixelGrid.Snap(host.Y, slot.Y + (slot.Height - layout.Height) / 2, scale);
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

            runs.Add((pixels, w, h, at));
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
            foreach (var (pixels, w, h, at) in runs)
            {
                var props = new D2D1_BITMAP_PROPERTIES1 { pixelFormat = Format, dpiX = (float)(96 * scale), dpiY = (float)(96 * scale) };
                ID2D1Bitmap1* run;
                fixed (byte* bits = pixels)
                {
                    dc->CreateBitmap(new D2D_SIZE_U { width = (uint)w, height = (uint)h }, bits, (uint)w * 4, &props, &run);
                }

                var dest = new D2D_RECT_F { left = at.X, top = at.Y, right = at.X + (float)(w / scale), bottom = at.Y + (float)(h / scale) };
                dc->DrawBitmap((ID2D1Bitmap*)run, &dest, 1, D2D1_INTERPOLATION_MODE.D2D1_INTERPOLATION_MODE_NEAREST_NEIGHBOR, null, null);
                run->Release();
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
