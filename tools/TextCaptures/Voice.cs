using System.Drawing;
using System.Numerics;
using Tomte.Core;
using Tomte.Core.Rambler;
using Tomte.Core.Text;

namespace TextCaptures;

/// <summary>The voice card, from its own layout: Insert highlighted with where it types, Ask highlighted, and Insert refused.</summary>
internal static partial class Program
{
    private const string Heard = "Banner set is done, copy needs one more pass, and the pricing review moved to Friday at ten.";

    private static List<Scene> VoiceScenes() =>
    [
        VoiceScene("voice-card", VoiceChoice.Insert, null, null),
        VoiceScene("voice-card-ask", VoiceChoice.Ask, null, null),
        VoiceScene("voice-card-refused", VoiceChoice.Insert, null, "The window you spoke into is no longer in front."),
        VoiceScene("voice-card-no-provider", VoiceChoice.Insert, VoiceChoices.NoProvider, null),
    ];

    private static Scene VoiceScene(string name, VoiceChoice highlighted, string? askOff, string? refused)
    {
        const float Width = (float)PanelSize.VoiceWidth;
        var layout = VoiceLayout(askOff, refused);
        return new(name, IslandGeometry.Panel(Width, layout.Height), c => Voice(c, layout, highlighted, askOff, refused));
    }

    private static VoiceCardLayout VoiceLayout(string? askOff, string? refused)
    {
        const float Width = (float)PanelSize.VoiceWidth;
        using var laid = TextLayout.Of(new TextRun(Heard, VoiceCardLayout.Words, Width - (2 * VoiceCardLayout.Side), VoiceCardLayout.WordLines));
        return new VoiceCardLayout(Width, mascot: true, laid.Height, refused ?? askOff ?? "");
    }

    // The card as VoiceCard draws it, with a made-up take: seven seconds of speech and a moment of silence.
    private static void Voice(Canvas c, VoiceCardLayout layout, VoiceChoice highlighted, string? askOff, string? refused)
    {
        var origin = new Vector2(-layout.Width / 2, 0);
        c.Pill();
        c.Mascot(shift: VoiceCardLayout.Seat.Shift);
        var mic = layout.Mic;
        c.Circle(new Vector2(mic.X + (mic.Width / 2), mic.Y + (mic.Height / 2)) + origin, mic.Width / 2, Theme.Raised);
        c.Glyph(Glyphs.Mic, new Vector2(mic.X + ((mic.Width - VoiceCardLayout.MicSize) / 2), mic.Y + ((mic.Height - VoiceCardLayout.MicSize) / 2)) + origin, VoiceCardLayout.MicSize, Theme.Ink);
        var (heard, length) = layout.Header(TimeSpan.FromSeconds(7));
        c.Text(heard, origin);
        c.Text(length, origin);
        var (lockAt, where) = layout.Where(VoiceCardLayout.PrivacyLine(null));
        c.Glyph(Glyphs.Lock, new Vector2(lockAt.X, lockAt.Y) + origin, VoiceCardLayout.LockSize, Theme.Ink3);
        c.Text(where, origin);

        var wave = layout.Wave;
        int bars = Waveform.TakeBars(7.2, wave.Width);
        for (int i = 0; i < bars; i++)
        {
            bool silent = i >= bars - 4;
            float h = silent ? Waveform.TakeMin : Waveform.TakeMin + ((Waveform.TakeHeight - Waveform.TakeMin) * (0.5f + (0.45f * MathF.Sin(i * 1.7f))));
            var bar = new RectangleF(wave.X + (i * (Waveform.TakeBar + Waveform.TakeGap)), wave.Y + ((wave.Height - h) / 2), Waveform.TakeBar, h);
            c.RoundRect(Offset(bar, origin), Waveform.TakeBar / 2, silent ? Theme.Silence : Theme.Ink);
        }

        var text = layout.Text;
        c.Text(new TextRun(Heard, VoiceCardLayout.Words, text.Width, VoiceCardLayout.WordLines), new Vector2(text.X, text.Y), origin);

        VoiceChoice[] order = [VoiceChoice.Insert, VoiceChoice.Ask, VoiceChoice.Note];
        for (int i = 0; i < order.Length; i++)
        {
            var choice = order[i];
            var r = layout.Actions[i];
            bool on = refused is null && choice == highlighted, enabled = choice != VoiceChoice.Ask || askOff is null;
            // Text is drawn on surfaces of its own, which a pushed layer would break: each part takes the opacity itself.
            float o = enabled ? 1 : Tomte.Core.Interaction.StateLook.DisabledOpacity;
            c.RoundRect(Offset(r, origin), VoiceCardLayout.ActionCorner, on ? Theme.Accent : Theme.Raised, o);
            uint ink = on ? Theme.OnAccent : Theme.Ink;
            float x = r.X + VoiceCardLayout.ActionPadX, right = r.Right - VoiceCardLayout.ActionPadX;
            if (on)
            {
                var cap = Tomte.Core.Keycap.EndingAt("Enter", right, r);
                c.RoundRect(Offset(cap, origin), Tomte.Core.Keycap.Corner, VoiceCardLayout.PrimaryKeyFill);
                c.Text("Enter", VoiceCardLayout.PrimaryKey, cap, origin);
                right = cap.X - VoiceCardLayout.GlyphGap;
            }

            if (choice != VoiceChoice.Insert)
            {
                var glyph = choice == VoiceChoice.Ask ? Glyphs.Sparkle : Glyphs.Pencil;
                c.Glyph(glyph, new Vector2(x, r.Y + ((r.Height - VoiceCardLayout.GlyphSize) / 2)) + origin, VoiceCardLayout.GlyphSize, choice == VoiceChoice.Ask && !on ? Theme.Accent : ink);
                x += VoiceCardLayout.GlyphSize + VoiceCardLayout.GlyphGap;
            }

            string label = choice == VoiceChoice.Insert && refused is not null ? "Copy" : VoiceChoices.Label(choice);
            var style = on ? VoiceCardLayout.Primary : VoiceCardLayout.Action;
            if (choice == VoiceChoice.Insert && refused is null)
            {
                float y = r.Y + ((r.Height - 34) / 2);
                c.Text(label, style, new RectangleF(x, y, right - x, 19), origin, o);
                c.Text("into Standup notes", on ? VoiceCardLayout.Into : TextStyle.Small, new RectangleF(x, y + 19, right - x, 15), origin, o);
            }
            else
            {
                c.Text(label, style, new RectangleF(x, r.Y, right - x, r.Height), origin, o);
            }
        }

        var reason = layout.ReasonLine;
        c.Text(reason with { Style = refused is null ? VoiceCardLayout.Reason : VoiceCardLayout.Reason with { Color = Theme.Warn } }, origin);
        foreach (var (capAt, keys, word) in layout.Footer(("Win Alt V", "hold to talk again"), [("E", "edit words"), ("Esc", "discard")]))
        {
            Keycap(c, capAt, keys, origin);
            c.Text(word, origin);
        }
    }
}
