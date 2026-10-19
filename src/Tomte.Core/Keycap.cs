using System.Drawing;
using Tomte.Core.Text;

namespace Tomte.Core;

/// <summary>
/// A key hint: the keys of one shortcut in one cap ("Ctrl Shift Enter"), mono 11 in the secondary ink on a raised fill
/// with a strong 1 DIP border. Footers pair each cap with the word for what it does.
/// </summary>
public static class Keycap
{
    public const float Height = 20, PadX = 6, Corner = 6, WordGap = 6;

    public static uint Fill => Theme.Raised;

    public static uint Border => Theme.LineStrong;

    public static TextStyle Style => new TextStyle(11, Color: Theme.Ink2, Align: TextAlign.Center) with { Family = TextStyle.Mono };

    /// <summary>A shortcut as its cap shows it ("Win+Alt+Space" as "Win Alt Space"), or null when none is set.</summary>
    public static string? Words(string chord) => Hotkey.TryParse(chord, out var key, out _) ? key.ToString().Replace('+', ' ') : null;

    /// <summary>How wide the cap for <paramref name="keys"/> is: the text, its padding and the border on both sides.</summary>
    public static float Width(string keys) => TextSlot.Measure(keys, Style, 400) + (2 * PadX) + 2;

    /// <summary>A cap of <paramref name="keys"/> whose right end is at <paramref name="right"/>, centred down <paramref name="row"/>.</summary>
    public static RectangleF EndingAt(string keys, float right, RectangleF row)
    {
        float w = Width(keys);
        return new(right - w, row.Y + ((row.Height - Height) / 2), w, Height);
    }

    /// <summary>
    /// A row of hints, each a cap and its word, from <paramref name="left"/> on, <paramref name="gap"/> apart; a hint that
    /// would pass <paramref name="right"/> is left out with every hint after it.
    /// </summary>
    public static List<(RectangleF Cap, string Keys, TextSlot Word)> Row(IEnumerable<(string Keys, string Word)> hints, float left, float right, float y, float height, TextStyle word, float gap)
    {
        var laid = new List<(RectangleF, string, TextSlot)>();
        float x = left;
        foreach (var (keys, said) in hints)
        {
            float cw = Width(keys), ww = TextSlot.Measure(said, word, 400);
            if (x + cw + WordGap + ww > right)
            {
                break;
            }

            var cap = new RectangleF(x, y + ((height - Height) / 2), cw, Height);
            laid.Add((cap, keys, new TextSlot(said, word, new RectangleF(cap.Right + WordGap, y, ww + 1, height))));
            x = cap.Right + WordGap + ww + gap;
        }

        return laid;
    }
}
