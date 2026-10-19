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
        var layout = new PaletteLayout(720, mascot: true, text: 0, chips[..2], send: true);
        // The prompt card sits after the tomte, whose art starts at the padding, one row tall with two chips first in it
        // and the text box after them.
        Assert.Equal(PaletteLayout.Pad - MascotArt.ArtLeftDips + MascotArt.SizeDips + 10, layout.Prompt.X, 3);
        Assert.Equal((14f, 52f), (layout.Prompt.Y, layout.Prompt.Height));
        Assert.Equal(720 - PaletteLayout.Pad, layout.Prompt.Right, 3);
        Assert.Equal(layout.Prompt.X + PaletteLayout.PromptInsetLeft, layout.Chips[0].Box.X, 3);
        Assert.Equal(layout.Prompt.Y + (layout.Prompt.Height / 2), layout.Chips[0].Box.Y + (layout.Chips[0].Box.Height / 2), 3);
        Assert.Equal(layout.Chips[^1].Box.Right + PaletteLayout.ChipEditorGap, layout.Editor.X, 3);
        Assert.Equal(PaletteLayout.ChipOn, layout.Chips[0].Label.Style);

        // Six chips leave the text box too little room: they take the card's rows and the text box goes under them.
        var wrapped = new PaletteLayout(720, mascot: true, text: 0, chips, send: true);
        Assert.True(wrapped.Chips[^1].Box.Y > wrapped.Chips[0].Box.Y, "the last chip wraps");
        Assert.All(wrapped.Chips, chip => Assert.True(chip.Box.Right <= wrapped.Send.X - PaletteLayout.SendGap + 0.01f));
        Assert.Equal(wrapped.Chips[0].Box.X, wrapped.Editor.X, 3);
        Assert.True(wrapped.Editor.Y > wrapped.Chips[^1].Box.Bottom);
        Assert.Equal(wrapped.Editor.Bottom + PaletteLayout.PromptInsetY, wrapped.Prompt.Bottom, 3);

        // No answer: the smallest palette.
        Assert.Equal(150, layout.Height(0));
        List<(string, TextStyle)> answer = [("Read notes.txt", PaletteLayout.StepText), ("", PaletteLayout.AnswerText), ("The dentist is on Monday at 10.", PaletteLayout.AnswerText)];
        var (runs, measured) = layout.Answer(answer);
        // Laid out from the answer's top in the column after the sparkle, one block under the other; an empty block takes
        // no room; the model's first paragraph stands in the lead style.
        Assert.Equal(18 + PaletteLayout.BlockGap + 27, measured, 1);
        Assert.Equal(2, runs.Count);
        Assert.Equal(0, runs[0].At.Y);
        Assert.Equal(18 + PaletteLayout.BlockGap, runs[1].At.Y, 1);
        Assert.Equal(PaletteLayout.Lead, runs[1].Run.Style);
        Assert.All(runs, r => Assert.Equal(layout.AnswerX, r.At.X, 1));
        Assert.Equal(PaletteLayout.Pad + PaletteLayout.AnswerInset + PaletteLayout.Gutter, layout.AnswerX);
        float height = layout.Height(measured);
        const float Foot = PaletteLayout.FootTop + PaletteLayout.FootHeight + PaletteLayout.Bottom;
        Assert.Equal(layout.AnswerTop + measured + PaletteLayout.Gap + Foot, Math.Max(height, 150), 1);
        // The band the answer shows in runs from under the card to above the footer.
        var band = layout.AnswerBand(height);
        Assert.Equal(layout.AnswerTop, band.Y);
        Assert.Equal(height - Foot - PaletteLayout.Gap, band.Bottom, 1);
        Assert.Equal(0, layout.MaxScroll(measured, height));

        // A long answer stops the palette at 480 and is laid out whole: the band scrolls over it.
        var longer = new List<(string, TextStyle)> { (string.Join(' ', Enumerable.Repeat("A sentence that keeps the answer going.", 120)), PaletteLayout.AnswerText) };
        var (whole, tall) = layout.Answer(longer);
        Assert.Equal(480, layout.Height(tall));
        using (var laid = TextLayout.Of(whole[0].Run))
        {
            Assert.Equal(laid.LineCount * 27, tall, 1);
        }

        Assert.Equal(tall - layout.AnswerBand(480).Height, layout.MaxScroll(tall, 480), 1);

        // The card grows with its lines up to four, and everything under it moves down with it.
        var grown = new PaletteLayout(720, mascot: true, text: 52, chips[..2], send: true);
        Assert.Equal(84, grown.Prompt.Height);
        Assert.Equal(layout.AnswerTop + 32, grown.AnswerTop, 1);
        Assert.Equal(112, new PaletteLayout(720, mascot: true, text: 200).Prompt.Height);
        // Without the tomte the card starts at the padding.
        Assert.Equal(16, new PaletteLayout(720, mascot: false).Prompt.X);
    }

    [Fact]
    public void TheModelsWordsAreReadAsMarkdownAndTheRestStaysPlain()
    {
        var layout = new PaletteLayout(720, mascot: true);
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
        // From the answer's column, the list's text hanging right of its bullet, the plain blocks 8 apart; the first
        // paragraph is the lead, the list after it in the answer's own style.
        float x = layout.AnswerX;
        Assert.Equal([x, x, x, x + DropResultLayout.Indent], runs.Take(4).Select(r => r.At.X));
        Assert.Equal(18 + PaletteLayout.BlockGap, runs[1].At.Y, 1);
        Assert.Equal((PaletteLayout.Lead, PaletteLayout.AnswerText), (runs[1].Run.Style, runs[3].Run.Style));
        Assert.Equal(runs[^1].At.Y + 21, measured, 1);
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
        var layout = new PaletteLayout(720, mascot: true);
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
    public void TheFooterSaysWhereItWentWhatItCostAndWhatTodayCost()
    {
        Assert.Equal("$0.12 / $10 today", PaletteLayout.SpendLine(0.12m, 10m));
        Assert.Equal("$1.50 / $2.5 today", PaletteLayout.SpendLine(1.5m, 2.5m));
        Assert.Equal("$1.50 today", PaletteLayout.SpendLine(1.5m, null));
        Assert.Equal("· $0.002", PaletteLayout.CostLine(0.002m));
        Assert.Equal("· $0.12", PaletteLayout.CostLine(0.12m));
        Assert.Equal("", PaletteLayout.CostLine(0));

        var foot = new PaletteLayout(720, mascot: true).Foot("Sent to Anthropic · claude-haiku-5-5", 2, 0.002m, 0.12m, 10m, 150);
        Assert.Equal("Sent to Anthropic · claude-haiku-5-5 · 2 tool steps", foot.Provider.Text);
        Assert.Equal(150 - PaletteLayout.Bottom - PaletteLayout.FootHeight, foot.Spend.Slot.Y);
        Assert.Equal(foot.Spend.Slot.Y - PaletteLayout.FootTop, foot.Divider.Y);
        // Left to right: the dot, the provider, the cost, then today's spend and the meter at the right end.
        Assert.True(foot.Dot.Right < foot.Provider.Slot.X && foot.Provider.Slot.Right < foot.Cost.Slot.X && foot.Cost.Slot.Right < foot.Spend.Slot.X);
        Assert.Equal(720 - PaletteLayout.Pad - PaletteLayout.AnswerInset, foot.Meter.Right, 0.01);
        Assert.Equal((PaletteLayout.MeterWidth, PaletteLayout.MeterHeight), (foot.Meter.Width, foot.Meter.Height));
        // A tiny spend still shows on the meter; the meter fills with the cap.
        Assert.Equal(PaletteLayout.MeterMin, foot.Fill);
        Assert.Equal(32, new PaletteLayout(720, mascot: true).Foot("", 0, 0, 5m, 10m, 150).Fill, 0.01);
        Assert.Equal(PaletteLayout.MeterWidth, new PaletteLayout(720, mascot: true).Foot("", 0, 0, 12m, 10m, 150).Fill, 0.01);
        Assert.True(new PaletteLayout(720, mascot: true).Foot("", 0, 0, 1m, null, 150).Meter.IsEmpty);
        Assert.Equal((Theme.Ok, Theme.Warn, Theme.Ink3), (PaletteLayout.StatusDot("Sent to Anthropic · m", false), PaletteLayout.StatusDot("Sent to Anthropic · m", true), PaletteLayout.StatusDot("Nothing sent", false)));
    }

    [Fact]
    public void AFinishedAnswersActionsLineUpWithItsWords()
    {
        string[] offered = [PaletteLayout.CopyId, PaletteLayout.NoteId, PaletteLayout.ShelfId, PaletteLayout.FollowId];
        var plain = new PaletteLayout(720, mascot: true);
        var layout = new PaletteLayout(720, mascot: true, actions: offered);
        float height = layout.Height(27);
        var actions = layout.Actions(height);
        Assert.Equal(["Copy", "Add to note", "Keep on shelf", "Follow up"], actions.Select(a => a.Label.Text));
        Assert.Equal(layout.AnswerX, actions[0].Box.X);
        Assert.Equal([false, false, false, true], actions.Select(a => a.Outlined));
        Assert.All(actions, a => Assert.Equal(PaletteLayout.ActionHeight, a.Box.Height));
        for (int i = 1; i < actions.Count; i++)
        {
            Assert.Equal(actions[i - 1].Box.Right + PaletteLayout.ActionGap, actions[i].Box.X, 3);
        }

        // The row takes its room between the answer and the footer.
        Assert.Equal(plain.Height(27) + PaletteLayout.ActionHeight + PaletteLayout.Gap, height, 1);
        Assert.Equal(layout.AnswerBand(height).Bottom + PaletteLayout.Gap, actions[0].Box.Y, 1);
        // Done, a pill says so in the same width.
        var copied = layout.Actions(height, [PaletteLayout.CopyId]);
        Assert.Equal(("Copied", actions[0].Box), (copied[0].Label.Text, copied[0].Box));
        Assert.Empty(plain.Actions(height));
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
