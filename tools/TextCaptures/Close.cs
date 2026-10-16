using Tomte.Core;
using Tomte.Shell;
using Windows.Win32.Graphics.Direct2D;
using Windows.Win32.Graphics.Direct3D11;

namespace TextCaptures;

/// <summary>
/// The pill closing and opening with its tomte, every third frame at 165 Hz on the island's own springs, at 100%, 125%
/// and 150%: the live outline and the tomte placed, sized and faded from it as the island does each frame. The content,
/// its fade and the shadow are left out.
/// Usage: TextCaptures --close &lt;output folder&gt;
/// </summary>
internal static unsafe partial class Program
{
    private static int CloseCaptures(string dir)
    {
        Directory.CreateDirectory(dir);
        var d2d = TextDraw.CreateDevice(out ID3D11Device* d3d);
        ID2D1DeviceContext* dc;
        d2d->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS.D2D1_DEVICE_CONTEXT_OPTIONS_NONE, &dc);
        var panels = IslandGeometry.Panel(PanelSize.Width, PanelSize.Shelf.H);
        (string Name, IslandGeometry From, IslandGeometry To)[] morphs =
        [
            ("compact-close", IslandGeometry.Compact, IslandGeometry.Hidden),
            ("compact-open", IslandGeometry.Hidden, IslandGeometry.Compact),
            ("panels-close", panels, IslandGeometry.Hidden),
        ];
        foreach (double scale in Scales)
        {
            foreach (var (name, from, to) in morphs)
            {
                // Opening from Hidden and every close are snappy.
                Spring width = new(), depth = new(), corner = new();
                Spring[] springs = [width, depth, corner];
                width.Jump(from.Width);
                depth.Jump(from.Depth);
                corner.Jump(from.Corner);
                foreach (var spring in springs)
                {
                    spring.Use(SpringPreset.Snappy);
                }

                (width.Target, depth.Target, corner.Target) = (to.Width, to.Depth, to.Corner);
                for (int frame = 0; ; frame++)
                {
                    bool done = Array.TrueForAll(springs, s => s.Settled);
                    var live = new IslandGeometry(width.Value, depth.Value, corner.Value);
                    if ((frame % 3 == 0 || done) && live.Depth >= 0.5)
                    {
                        var canvas = new Canvas(dc, live, scale);
                        canvas.Pill();
                        canvas.Mascot();
                        canvas.Save(Path.Combine(dir, $"{scale * 100:0}-{name}-{frame:D3}.png"));
                        canvas.Dispose();
                    }

                    if (done)
                    {
                        break;
                    }

                    foreach (var spring in springs)
                    {
                        spring.Step(1.0 / 165);
                    }
                }
            }
        }

        dc->Release();
        d2d->Release();
        d3d->Release();
        return 0;
    }
}
