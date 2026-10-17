using System.Diagnostics;
using System.Numerics;
using Tomte.Core;
using Tomte.Core.Storage;
using Tomte.Core.Text;
using Xunit;

namespace Tomte.Tests;

/// <summary>The text boxes' logic: the prompt's keys, the note binding, the scale, the paints, the caret, the palette's layout.</summary>
public sealed class TypingTests : IDisposable
{
    private const uint KeyDown = 0x0100, KeyUp = 0x0101, Char = 0x0102;
    private const nuint Return = 0x0D, ProcessKey = 0xE5;

    private readonly string dir = Path.Combine(Path.GetTempPath(), "tomte-typing-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void EnterSendsAndShiftEnterIsANewLine()
    {
        var keys = new PromptKeys();
        // Enter: the key down sends and is swallowed, and so is the carriage return the loop makes from it.
        Assert.True(keys.Sends(KeyDown, Return, shift: false, out bool swallow));
        Assert.True(swallow);
        Assert.False(keys.Sends(Char, '\r', shift: false, out swallow));
        Assert.True(swallow);
        Assert.False(keys.Sends(KeyUp, Return, shift: false, out swallow));
        Assert.False(swallow);

        // Shift+Enter goes to the text box whole: it is the new line.
        Assert.False(keys.Sends(KeyDown, Return, shift: true, out swallow));
        Assert.False(swallow);
        Assert.False(keys.Sends(Char, '\r', shift: true, out swallow));
        Assert.False(swallow);

        // Ctrl+Enter sends too; its line feed is swallowed.
        Assert.True(keys.Sends(KeyDown, Return, shift: false, out _));
        Assert.False(keys.Sends(Char, '\n', shift: false, out swallow));
        Assert.True(swallow);

        // Enter while an input method composes arrives as VK_PROCESSKEY: it commits, it never sends.
        Assert.False(keys.Sends(KeyDown, ProcessKey, shift: false, out swallow));
        Assert.False(swallow);
        // A carriage return that no send started is typed.
        Assert.False(keys.Sends(Char, '\r', shift: false, out swallow));
        Assert.False(swallow);
    }

    [Fact]
    public async Task ARamblerNoteThatLandsWhileTypingIsKept()
    {
        var note = await Note("Milk");
        var binding = new NoteBinding(note);
        // A whole-text write leaves the caret where it was, here at the start.
        var (text, caret) = binding.Open();
        Assert.Equal(("Milk", 0), (text, caret));

        // The user types; the text box holds its own copy, as the system's text services do.
        string box = Type(binding, text, 4, ", eggs");
        Assert.Equal("Milk, eggs", note.Text);

        // A rambler note is saved on a worker. The next key first takes it in, then goes where the caret was.
        await note.AppendAsync("Call the dentist", new DateTime(2026, 10, 14, 9, 30, 0));
        if (binding.Pull() is { } change)
        {
            box = change.Apply(box);
        }

        box = Type(binding, box, 10, "!");
        Assert.StartsWith("Milk, eggs!", note.Text);
        Assert.Contains("Call the dentist", note.Text);
        Assert.Equal(note.Text, box);

        // The race: a note lands after the key was applied to the box but before the box's text reached the note.
        string typed = box.Insert(11, "?");
        await note.AppendAsync("Buy stamps", new DateTime(2026, 10, 14, 9, 31, 0));
        string? now = binding.Typed(typed, 12);
        Assert.NotNull(now);
        Assert.StartsWith("Milk, eggs!?", note.Text);
        Assert.Contains("Call the dentist", note.Text);
        Assert.Contains("Buy stamps", note.Text);
        Assert.Equal(note.Text, now);
        Assert.Equal(12, note.Caret);
    }

    [Fact]
    public async Task AWriteOverTheSameWordsKeepsTheWriteNotTheKey()
    {
        var note = await Note("one two three");
        var binding = new NoteBinding(note);
        binding.Open();
        // The AI tool rewrites a word while the user's Backspaces over it are on their way: what the tool wrote stays whole.
        note.Text = "one TWO three";
        string? now = binding.Typed("one  three", 4);
        Assert.Equal("one TWO three", now);
        Assert.Equal("one TWO three", note.Text);
    }

    [Fact]
    public async Task TheBoxNeverTypesOverANoteItHasNotShown()
    {
        var note = await Note("kept");
        var binding = new NoteBinding(note);
        binding.Open();
        note.Text = "kept, and more";
        // A key based on what the box showed before goes onto the note as it is now.
        Assert.Equal("kept!, and more", binding.Typed("kept!", 5));
        Assert.Equal("kept!, and more", note.Text);
        Assert.False(note.TryEdit("stale", "lost", 0));
        Assert.Equal("kept!, and more", note.Text);
    }

    [Fact]
    public void SplicesNeverCutASurrogatePair()
    {
        var change = Splice.Between("a\U0001F600b", "a\U0001F601b");
        Assert.Equal(new Splice(1, 2, "\U0001F601"), change);
        Assert.Equal("a\U0001F601b", change.Apply("a\U0001F600b"));
        var append = Splice.Between("note", "note\n\nmore");
        Assert.Equal(new Splice(4, 0, "\n\nmore"), append);
        // A caret at the end stays before what was appended there, one past it moves with the text.
        Assert.Equal(4, append.Map(4));
        Assert.Equal(1, append.Map(1));
        Assert.True(Splice.Between("same", "same").IsEmpty);
    }

    [Theory]
    // A per-monitor-aware process on a 150% primary screen: the services' units are pixels at 144 DPI.
    [InlineData(1.0, 144u, 0.6667f)]
    [InlineData(1.25, 144u, 0.8333f)]
    [InlineData(1.5, 144u, 1f)]
    // The same at 100% on the primary screen.
    [InlineData(1.0, 96u, 1f)]
    [InlineData(1.25, 96u, 1.25f)]
    [InlineData(1.5, 96u, 1.5f)]
    public void TheMonitorsScaleReachesTheServicesAsAZoom(double scale, uint systemDpi, float zoom)
    {
        Assert.Equal(zoom, EditorScale.Zoom(scale, systemDpi), 3);
        // The services take the zoom as the client size over the extent: 600 px reported against its extent.
        int extent = EditorScale.Extent(600, systemDpi, EditorScale.Zoom(scale, systemDpi));
        Assert.Equal(600 * 2540.0 / systemDpi / extent, EditorScale.Zoom(scale, systemDpi), 2);
        Assert.Equal(EditorScale.IsNone(EditorScale.Zoom(scale, systemDpi)), Math.Abs(zoom - 1) < 0.001);
        // A BeginDraw offset in pixels is a translation in DIPs on a context at the services' DPI.
        Assert.Equal(150 * 96f / systemDpi, EditorScale.Translation(150, systemDpi), 3);
    }

    [Fact]
    public void FontSizesGoInTwips()
    {
        Assert.Equal(195, EditorScale.Twips(13));
        Assert.Equal(210, EditorScale.Twips(14));
    }

    [Fact]
    public void ManyRepaintsInOneTurnPaintOnce()
    {
        var gate = new PaintGate();
        Assert.True(gate.Invalidate(new PixelRect(10, 10, 20, 5)));
        Assert.False(gate.Invalidate(new PixelRect(0, 30, 5, 5)));
        Assert.False(gate.Invalidate(new PixelRect(50, 0, 100, 2)));
        Assert.False(gate.Invalidate(new PixelRect(5, 5, 0, 10)));
        // One paint of the union, cut to the surface.
        Assert.Equal(new PixelRect(0, 0, 120, 35), gate.Take(120, 200));
        Assert.Null(gate.Take(120, 200));
        Assert.True(gate.Invalidate(new PixelRect(200, 200, 10, 10)));
        Assert.Null(gate.Take(120, 200));
    }

    [Fact]
    public void TheCaretBlinksAtTheSystemRateUntilTheTimeout()
    {
        // Windows' defaults: 530 ms on, 530 off, for 5 s after the last key, then solid.
        Assert.Equal(new CaretBlink(TimeSpan.FromMilliseconds(1060), 5), CaretBlink.Plan(530, 5000));
        Assert.Equal(new CaretBlink(TimeSpan.FromMilliseconds(400), 1), CaretBlink.Plan(200, 100));
        // Blinking turned off: no animation at all.
        Assert.Null(CaretBlink.Plan(uint.MaxValue, 5000));
        Assert.Null(CaretBlink.Plan(0, 5000));
        // A caret set never to stop blinking.
        Assert.Equal(0, CaretBlink.Plan(530, uint.MaxValue)!.Value.Iterations);
    }

    [Fact]
    public void ThePaletteGrowsWithItsAnswerUpTo480()
    {
        ChipSpec[] chips = [new("Region", ChipIcon.Region, true), new("Selection", ChipIcon.Selection, false), new("Window", ChipIcon.Window, false), new("Screenshot", ChipIcon.Screenshot, false), new("Shelf", ChipIcon.Shelf, true), new("Note", ChipIcon.Note, false)];
        var layout = new PaletteLayout(640, mascot: true, text: 0, chips);
        // The prompt box sits after the tomte, whose art starts at the padding, one line tall; the chips that do not fit
        // start a second row.
        Assert.Equal(PaletteLayout.Pad - MascotArt.ArtLeftDips + MascotArt.SizeDips + 10, layout.Prompt.X, 3);
        Assert.Equal((12f, 36f), (layout.Prompt.Y, layout.Prompt.Height));
        Assert.Equal(640 - PaletteLayout.Pad, layout.Prompt.Right, 3);
        Assert.Equal(58, layout.ChipsTop);
        Assert.Equal(58, layout.Chips[0].Box.Y);
        Assert.True(layout.Chips[^1].Box.Y > layout.ChipsTop, "the last chip wraps");
        Assert.All(layout.Chips, chip => Assert.True(chip.Box.Right <= 626.01f));
        Assert.Equal(PaletteLayout.ChipOn, layout.Chips[4].Label.Style);

        // No answer: the smallest palette while the chips take one row; a second row makes it taller.
        Assert.Equal(150, new PaletteLayout(640, mascot: true, text: 0, chips[1..]).Height(0));
        Assert.Equal(layout.AnswerTop + PaletteLayout.FootHeight + PaletteLayout.Bottom, layout.Height(0), 1);
        List<(string, TextStyle)> answer = [("Read notes.txt", PaletteLayout.StepText), ("", PaletteLayout.AnswerText), ("The dentist is on Monday at 10.", PaletteLayout.AnswerText)];
        var (runs, measured) = layout.Answer(answer);
        // Laid out from the answer's top, one block under the other; an empty block takes no room.
        Assert.Equal(18 + PaletteLayout.BlockGap + 20, measured, 1);
        Assert.Equal(2, runs.Count);
        Assert.Equal(0, runs[0].At.Y);
        Assert.Equal(18 + PaletteLayout.BlockGap, runs[1].At.Y, 1);
        float height = layout.Height(measured);
        Assert.Equal(layout.AnswerTop + measured + PaletteLayout.Gap + PaletteLayout.FootHeight + PaletteLayout.Bottom, Math.Max(height, 150), 1);
        // The band the answer shows in runs from under the chips to above the footer.
        var band = layout.AnswerBand(height);
        Assert.Equal(layout.AnswerTop, band.Y);
        Assert.Equal(height - PaletteLayout.Bottom - PaletteLayout.FootHeight - PaletteLayout.Gap, band.Bottom, 1);
        Assert.Equal(0, layout.MaxScroll(measured, height));

        // A long answer stops the palette at 480 and is laid out whole: the band scrolls over it.
        var longer = new List<(string, TextStyle)> { (string.Join(' ', Enumerable.Repeat("A sentence that keeps the answer going.", 120)), PaletteLayout.AnswerText) };
        var (whole, tall) = layout.Answer(longer);
        Assert.Equal(480, layout.Height(tall));
        using (var laid = TextLayout.Of(whole[0].Run))
        {
            Assert.Equal(laid.LineCount * 20, tall, 1);
        }

        Assert.Equal(tall - layout.AnswerBand(480).Height, layout.MaxScroll(tall, 480), 1);

        // The prompt grows with its lines up to about four, and everything under it moves down with it.
        var grown = new PaletteLayout(640, mascot: true, text: 52, chips);
        Assert.Equal(70, grown.Prompt.Height);
        Assert.Equal(layout.AnswerTop + 34, grown.AnswerTop, 1);
        Assert.Equal(90, new PaletteLayout(640, mascot: true, text: 200).Prompt.Height);
        // Without the tomte the box starts at the padding.
        Assert.Equal(14, new PaletteLayout(640, mascot: false).Prompt.X);
    }

    [Fact]
    public void TheModelsWordsAreReadAsMarkdownAndTheRestStaysPlain()
    {
        var layout = new PaletteLayout(640, mascot: true);
        List<(string, TextStyle)> answer =
        [
            ("Read **notes.txt**", PaletteLayout.StepText),
            ("You spent **$3,720** in *March*:\n- rent `$2,000`\n- food", PaletteLayout.AnswerText),
            ("The request **failed**", PaletteLayout.ErrorText),
        ];
        var (runs, measured) = layout.Answer(answer);
        Assert.Equal(["Read **notes.txt**", "You spent $3,720 in March:", "•", "rent $2,000", "•", "food", "The request **failed**"], runs.Select(r => r.Run.Text));
        Assert.Equal([(10, 6)], runs[1].Run.Bold);
        Assert.Equal([(20, 5)], runs[1].Run.Italic);
        Assert.Equal([(5, 6)], runs[3].Run.Code);
        // From the palette's padding, the list's text hanging right of its bullet, the plain blocks 6 apart as before.
        Assert.Equal([14, 14, 14, 14 + DropResultLayout.Indent], runs.Take(4).Select(r => r.At.X));
        Assert.Equal(18 + PaletteLayout.BlockGap, runs[1].At.Y, 1);
        Assert.Equal(runs[^1].At.Y + 20, measured, 1);
        // The same answer again takes what was measured the first time and lays out the same.
        var (again, height) = layout.Answer(answer);
        Assert.Equal(runs, again);
        Assert.Equal(measured, height);

        // While it streams, bold not closed yet waits instead of showing its asterisks; a finished answer shows a lone *.
        Assert.Equal("You spent", Assert.Single(layout.Answer([("You spent **$3,", PaletteLayout.AnswerText)], streaming: true).Runs).Run.Text);
        Assert.Equal("5 * 3", Assert.Single(layout.Answer([("5 * 3", PaletteLayout.AnswerText)]).Runs).Run.Text);
        // Only the last block is still coming in: text before a tool step is finished as it is.
        List<(string, TextStyle)> stepped = [("Let me **check", PaletteLayout.AnswerText), ("Read notes.txt", PaletteLayout.StepText)];
        Assert.Equal("Let me **check", layout.Answer(stepped, streaming: true).Runs[0].Run.Text);
        // Nothing settled yet takes no room.
        Assert.Equal(0, layout.Answer([("**", PaletteLayout.AnswerText)], streaming: true).Height);
    }

    [Fact]
    public void TheAnswerScrollsWithinItsLengthAndFollowsItsEndUntilScrolledUp()
    {
        var layout = new PaletteLayout(640, mascot: true);
        const float height = 480;
        float band = layout.AnswerBand(height).Height;
        float answer = band + 300;

        // Clamped at both ends; a notch is three lines.
        Assert.Equal(0, layout.Scrolled(0, 120, answer, height));
        Assert.Equal(300, layout.Scrolled(300, -120, answer, height));
        Assert.Equal(300 - PaletteLayout.WheelStep, layout.Scrolled(300, 120, answer, height));
        Assert.Equal(0, layout.Scrolled(10, 0, band - 5, height) + layout.MaxScroll(band - 5, height));

        // Pinned, a growing answer keeps its newest line in view.
        Assert.Equal(300, layout.Follow(0, pinned: true, answer, height));
        Assert.Equal(340, layout.Follow(300, pinned: true, answer + 40, height));
        // Scrolled up, it stays where the user left it while it grows, and is clamped when it shrinks.
        float up = layout.Scrolled(300, 120, answer, height);
        Assert.False(layout.AtEnd(up, answer, height));
        Assert.Equal(up, layout.Follow(up, pinned: false, answer + 40, height));
        Assert.Equal(0, layout.Follow(up, pinned: false, band - 10, height));
        // Scrolled back to the end, it is pinned again; an answer that fits is always at its end.
        float back = layout.Scrolled(up, -240, answer + 40, height);
        Assert.Equal(340, back);
        Assert.True(layout.AtEnd(back, answer + 40, height));
        Assert.True(layout.AtEnd(0, band - 10, height));
    }

    [Fact]
    public void AltF4AlwaysHidesTheIslandAndEscOnlyWhileABoxHasTheKeys()
    {
        const uint KeyDownMessage = 0x0100, SysKeyDown = 0x0104;
        const nuint Escape = 0x1B, F4 = 0x73;
        Assert.True(IslandKeys.Hides(SysKeyDown, F4, boxHasKeys: false));
        Assert.True(IslandKeys.Hides(SysKeyDown, F4, boxHasKeys: true));
        Assert.True(IslandKeys.Hides(KeyDownMessage, Escape, boxHasKeys: true));
        Assert.False(IslandKeys.Hides(KeyDownMessage, Escape, boxHasKeys: false));
        // F4 on its own and Alt with other keys are not the island's.
        Assert.False(IslandKeys.Hides(KeyDownMessage, F4, boxHasKeys: true));
        Assert.False(IslandKeys.Hides(SysKeyDown, 0x41, boxHasKeys: true));
    }

    [Fact]
    public void KeysTypedBeforeTheNoteOpensAreKeptAsText()
    {
        var kept = new System.Text.StringBuilder();
        foreach (char c in "buy milkk\b\rand eggs\u0001\b\b\b\b\tx")
        {
            EditorText.Keep(kept, c);
        }

        // Backspace takes one back, Enter is a new line, a Ctrl shortcut is not text.
        Assert.Equal("buy milk\nand \tx", kept.ToString());
        kept.Clear();
        EditorText.Keep(kept, '\b');
        Assert.Equal("", kept.ToString());
    }

    [Fact]
    public async Task TheBindingCountsLineBreaksAsTheBoxDoes()
    {
        Assert.Equal("a\nb\nc\nd", EditorText.Lines("a\r\nb\rc\vd"));
        var note = await Note("one\r\ntwo");
        // The caret after "two": index 8 in the note, 7 in the box.
        note.Edit(note.Text, 8);
        var binding = new NoteBinding(note);
        Assert.Equal(("one\ntwo", 7), binding.Open());
        // An append to the note lands at the box's end, not two characters past it.
        note.Text = "one\r\ntwo\r\nthree";
        Assert.Equal(new Splice(7, 0, "\nthree"), binding.Pull());
        Assert.Null(binding.Pull());
        // The box's first change writes one form back to the note.
        Assert.Null(binding.Typed("one!\ntwo\nthree", 4));
        Assert.Equal("one!\ntwo\nthree", note.Text);
    }

    [Fact]
    public void TheFooterSaysWhatWasSpent()
    {
        Assert.Equal("0 tool steps · $0.03 today of $2.00 cap", PaletteLayout.SpendLine(0, 0.03m, 2m));
        Assert.Equal("1 tool step · $1.50 today", PaletteLayout.SpendLine(1, 1.5m, null));
        var foot = new PaletteLayout(640, mascot: true).Foot("Nothing sent", 0, 0, 2m, 150);
        Assert.Equal("Nothing sent", foot[0].Text);
        Assert.Equal(150 - PaletteLayout.Bottom - PaletteLayout.FootHeight, foot[1].Slot.Y);
        Assert.Equal(626, foot[1].Slot.Right, 0.01);
    }

    /// <summary>
    /// The real text boxes, driven offscreen by tools/TextCaptures: a message-only window, keys straight into the
    /// system's text services, no input sent and nothing shown. It checks Enter and Shift+Enter in the prompt, the note
    /// binding with a rambler note landing mid-typing, one paint per turn, the caret, and that hiding releases the
    /// services, their timers and msftedit.dll.
    /// </summary>
    [Fact]
    public void TheTextBoxesWorkOffscreen()
    {
        Directory.CreateDirectory(dir);
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "TextCaptures.exe"), ["--typing", dir, "--no-measure"])
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var check = Process.Start(start)!;
        string output = check.StandardOutput.ReadToEnd();
        Assert.True(check.WaitForExit(60_000));
        Assert.True(check.ExitCode == 0, output);
        Assert.DoesNotContain("FAIL", output);
    }

    private async Task<ScratchpadNote> Note(string text)
    {
        Directory.CreateDirectory(dir);
        var note = new ScratchpadNote(new TomteDb(Path.Combine(dir, "tomte-" + Guid.NewGuid().ToString("N") + ".db")));
        await note.LoadAsync();
        note.Text = text;
        return note;
    }

    // What the text box does with typed characters: insert at the caret, then hand its whole text to the binding.
    private static string Type(NoteBinding binding, string box, int caret, string typed)
    {
        foreach (char c in typed)
        {
            box = box.Insert(caret++, c.ToString());
            if (binding.Typed(box, caret) is { } now)
            {
                box = now;
            }
        }

        return box;
    }
}
