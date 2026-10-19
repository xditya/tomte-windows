using System.Drawing;
using System.Globalization;
using Tomte.Core.Text;

namespace Tomte.Core.Rambler;

/// <summary>
/// The card the words show on after a hold, in DIPs from its top-left corner: a header with the mic, "Heard you", how
/// long the take was and where it was turned into text; the take's waveform; the words; Insert, Ask Tomte and Add to note
/// in a row; a line on why one of them is off when one is; and the key hints along the bottom.
/// <paramref name="words"/> is the height of the words as shown (the text box's while they are edited); <paramref name="line"/>
/// is the reason line, empty for none.
/// </summary>
public sealed class VoiceCardLayout(float width, bool mascot, float words, string line = "")
{
    public const float Top = 16, Side = 18, Bottom = 18, Gap = 16, Corner = 28;

    /// <summary>The header: the mic in a raised disc, then the words, then the lock before the line on where the words were made.</summary>
    public const float HeaderHeight = 22, Disc = 22, MicSize = 13, LockSize = 13, HeaderGap = 10;

    /// <summary>The take: bars 3 wide and 3 apart, up to 34 high.</summary>
    public const float WaveHeight = 34, WaveInset = 2;

    /// <summary>The three actions: 52 high, Insert 1.4 times as wide as each of the others.</summary>
    public const float ActionHeight = 52, ActionGap = 8, ActionCorner = 14, ActionPadX = 14, InsertShare = 1.4f, GlyphSize = 16, GlyphGap = 10;

    public const float LineHeight = 16, FootHeight = Keycap.Height, FootGap = 14;

    /// <summary>The words as heard: 20/500, up to four lines before they are cut.</summary>
    public static readonly TextStyle Words = TextStyle.Hero with { LineHeight = 29 };

    public const int WordLines = 4;

    public static readonly TextStyle Heard = new(13, 500);
    public static readonly TextStyle Duration = new TextStyle(12, Color: Theme.Ink3) with { Family = TextStyle.Mono };
    public static readonly TextStyle Privacy = TextStyle.Secondary with { Color = Theme.Ink3, Align = TextAlign.Trailing };
    public static readonly TextStyle Action = new(14, 500);
    public static TextStyle Primary => new(14, 600, Theme.OnAccent);
    public static TextStyle Into => new(11, Color: Theme.WithOpacity(Theme.OnAccent, 0.8f));
    public static TextStyle PrimaryKey => Keycap.Style with { Color = Theme.OnAccent };
    public static readonly TextStyle Reason = TextStyle.Small;
    public static readonly TextStyle Foot = TextStyle.Secondary with { Color = Theme.Ink3 };

    /// <summary>The highlighted action's keycap: the accent's own ink at 12%.</summary>
    public static uint PrimaryKeyFill => Theme.WithOpacity(Theme.OnAccent, 0.12f);

    /// <summary>The tomte beside the header, centred on it, as far in as the card's content.</summary>
    public static readonly MascotSeat Seat = new(Top + (HeaderHeight / 2), Side);

    public const string HeardYou = "Heard you";

    public float Width => width;

    /// <summary>Where the header starts: after the tomte, or at the side padding with it off.</summary>
    public float Left => mascot ? Seat.After(HeaderGap) : Side;

    public RectangleF Mic => new(Left, Top, Disc, Disc);

    /// <summary>"Heard you", then the take's length after it.</summary>
    public (TextSlot Heard, TextSlot Duration) Header(TimeSpan take)
    {
        float x = Mic.Right + HeaderGap, w = TextSlot.Measure(HeardYou, Heard, 200);
        string length = Length(take);
        return (new(HeardYou, Heard, new RectangleF(x, Top, w + 1, HeaderHeight)),
            new(length, Duration, new RectangleF(x + w + HeaderGap, Top, TextSlot.Measure(length, Duration, 100) + 1, HeaderHeight)));
    }

    /// <summary>The line on where the words were made, right-aligned, and the lock before it.</summary>
    public (RectangleF Lock, TextSlot Line) Where(string privacy)
    {
        float right = width - Side, w = TextSlot.Measure(privacy, Privacy, width / 2);
        var line = new RectangleF(right - w, Top, w, HeaderHeight);
        return (new RectangleF(line.X - HeaderGap - LockSize, Top + ((HeaderHeight - LockSize) / 2), LockSize, LockSize), new TextSlot(privacy, Privacy, line));
    }

    /// <summary>The waveform's band: as wide as the card's content, under the header.</summary>
    public RectangleF Wave => new(Side + WaveInset, Top + HeaderHeight + Gap, width - (2 * (Side + WaveInset)), WaveHeight);

    /// <summary>The words, under the waveform, as wide as the card's content.</summary>
    public RectangleF Text => new(Side, Wave.Bottom + Gap, width - (2 * Side), words);

    /// <summary>Insert, Ask Tomte and Add to note, left to right, under the words.</summary>
    public IReadOnlyList<RectangleF> Actions
    {
        get
        {
            float y = Text.Bottom + Gap, unit = (width - (2 * Side) - (2 * ActionGap)) / (InsertShare + 2);
            float insert = unit * InsertShare;
            return
            [
                new(Side, y, insert, ActionHeight),
                new(Side + insert + ActionGap, y, unit, ActionHeight),
                new(Side + insert + unit + (2 * ActionGap), y, unit, ActionHeight),
            ];
        }
    }

    /// <summary>Why an action is off or could not type, under the row; empty when there is nothing to say.</summary>
    public TextSlot ReasonLine => new(line, Reason, new RectangleF(Side, Actions[0].Bottom + (Gap / 2), width - (2 * Side), line.Length > 0 ? LineHeight : 0));

    private float FootTop => (line.Length > 0 ? ReasonLine.Slot.Bottom : Actions[0].Bottom) + Gap;

    /// <summary>The key hints: the first (when there is one) at the left, the rest from the right end.</summary>
    public List<(RectangleF Cap, string Keys, TextSlot Word)> Footer((string Keys, string Word)? first, IReadOnlyList<(string Keys, string Word)> right)
    {
        float y = FootTop, to = width - Side;
        var end = Keycap.Row(right, 0, float.MaxValue, y, FootHeight, Foot, FootGap);
        float endWidth = end.Count > 0 ? end[^1].Word.Slot.Right : 0;
        var laid = first is { } hint ? Keycap.Row([hint], Side, to - endWidth - FootGap, y, FootHeight, Foot, FootGap) : [];
        float shift = to - endWidth;
        laid.AddRange(end.Select(e => (e.Cap with { X = e.Cap.X + shift }, e.Keys, e.Word with { Slot = e.Word.Slot with { X = e.Word.Slot.X + shift } })));
        return laid;
    }

    /// <summary>The card's height for what it shows.</summary>
    public float Height => FootTop + FootHeight + Bottom;

    /// <summary>The take's length as minutes and seconds: "0:07".</summary>
    public static string Length(TimeSpan take) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)take.TotalMinutes}:{take.Seconds:00}");

    /// <summary>
    /// Where the words were made, which must be true: on this PC with nothing sent for the local model, else the name of
    /// the speech provider the audio went to.
    /// </summary>
    public static string PrivacyLine(string? provider) =>
        provider is null ? "Transcribed on this PC · nothing sent" : $"Transcribed by {provider} · audio sent";

    /// <summary>
    /// The name of what Insert types into, from its window's title: the part before the app's name ("Standup notes" from
    /// "Standup notes - Notepad"); null for a window with no title.
    /// </summary>
    public static string? Target(string title)
    {
        string name = title.Trim();
        foreach (string dash in new[] { " - ", " — ", " – ", " | " })
        {
            int at = name.LastIndexOf(dash, StringComparison.Ordinal);
            if (at > 0)
            {
                name = name[..at].Trim();
                break;
            }
        }

        return name.Length > 0 ? name : null;
    }
}
