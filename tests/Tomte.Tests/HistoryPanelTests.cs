using System.Numerics;
using Tomte.Core;
using Tomte.Core.Interaction;
using Xunit;

namespace Tomte.Tests;

/// <summary>The History tab's layout and the AI box's history button and follow-up line.</summary>
public class HistoryPanelTests
{
    private static readonly DateTimeOffset Morning = AiHistoryTests.Morning;

    private static AiExchange Talk(string key, DateTimeOffset at, string question = "What is this?") => AiHistoryTests.Talk(key, at, question);

    private static readonly Vector2 Body = new((float)PanelSize.History.W, (float)PanelSize.History.H - PanelSpace.TabStripHeight);

    private static List<AiExchange> Rows(int count) =>
        [.. Enumerable.Range(0, count).Select(i => Talk($"k{i}", Morning.AddMinutes(-i), $"Question {i}"))];

    [Fact]
    public void RowsStackNewestFirstAndOnlyThoseInViewAreTargets()
    {
        var layout = new HistoryPanelLayout(Body, Rows(20), opened: null);

        Assert.Equal(-1, layout.Opened);
        for (int i = 1; i < layout.Rows.Count; i++)
        {
            Assert.Equal(layout.Rows[i - 1].Bottom + HistoryPanelLayout.RowGap, layout.Rows[i].Y, 0.01);
        }

        var targets = layout.Targets(0);
        Assert.All(targets, t => Assert.Equal((TargetKind.Row, 1), (t.Kind, t.Group)));
        Assert.All(targets, t => Assert.True(layout.List.Contains(t.Bounds), $"{t.Id} at {t.Bounds}"));
        Assert.True(targets.Count < 20);
        Assert.Equal("row:k0", targets[0].Id);

        // Scrolled to the end, the last row is in view and the first is not.
        var end = layout.Targets(layout.MaxScroll);
        Assert.Equal("row:k19", end[^1].Id);
        Assert.DoesNotContain(end, t => t.Id == "row:k0");
        Assert.Equal(layout.MaxScroll, layout.Scrolled(layout.MaxScroll, -120 * 50));
        Assert.Equal(0, layout.Scrolled(0, 120));
        Assert.Equal("row:k1", layout.At(new Vector2(layout.List.X + 50, layout.List.Y + layout.Rows[1].Y + 10), 0)?.Id);
    }

    [Fact]
    public void AnOpenedRowShowsItsConversationAndTwoButtonsUnderIt()
    {
        var rows = Rows(5);
        rows[1] = rows[1] with { Turns = [new AiTurn("First", ["lasso"], ["Saved a note"], "**Bold** answer"), new AiTurn("Second", [], [], "", "Timed out after 120 s.")] };
        var layout = new HistoryPanelLayout(Body, rows, opened: "k1");

        Assert.Equal(1, layout.Opened);
        var card = layout.Rows[1];
        Assert.True(card.Height > HistoryPanelLayout.RowHeight + 100);
        Assert.All(layout.Conversation, run => Assert.True(run.At.Y >= card.Y + HistoryPanelLayout.RowHeight && run.At.Y < card.Bottom));
        Assert.Contains(layout.Conversation, run => run.Run.Text == "With lasso");
        Assert.Contains(layout.Conversation, run => run.Run.Text == "Timed out after 120 s." && run.Run.Style == HistoryPanelLayout.ErrorStyle);
        Assert.True(layout.ContinueButton.Y > layout.Conversation.Max(r => r.At.Y));
        Assert.Equal(layout.ContinueButton.Right + HistoryPanelLayout.ButtonGap, layout.CopyButton.X, 0.01);
        Assert.Equal(StateLook.ActionHeight, layout.ContinueButton.Height);

        // Tab order: the rows that show, as one group, then Continue, then Copy answer, which is off when the last turn has
        // no answer. Opening scrolled the row into view with its buttons.
        var targets = layout.Targets(layout.Reveal(0, 1));
        var ids = targets.Select(t => t.Id).ToList();
        Assert.Equal([HistoryPanelLayout.ContinueId, HistoryPanelLayout.CopyId], ids.Skip(ids.Count - 2));
        Assert.All(targets.Take(targets.Count - 2), t => Assert.Equal((TargetKind.Row, 1), (t.Kind, t.Group)));
        Assert.True(targets.Single(t => t.Id == "row:k1").Checked);
        Assert.False(targets.Single(t => t.Id == HistoryPanelLayout.CopyId).Enabled);
        Assert.Equal("", rows[1].LastAnswer);
    }

    [Fact]
    public void OpeningARowScrollsToShowIt()
    {
        var rows = Rows(12);
        rows[10] = rows[10] with { Turns = [new AiTurn("Long", [], [], "- one\n- two\n\n" + string.Join("\n\n", Enumerable.Range(0, 30).Select(i => $"Paragraph {i}.")))] };
        var layout = new HistoryPanelLayout(Body, rows, opened: "k10");

        float scroll = layout.Reveal(0, 10);

        // Read as Markdown: a list item's marker is a run of its own.
        Assert.Equal(2, layout.Conversation.Count(run => run.Run.Text == "•"));

        // Taller than the list: its top comes to the list's top.
        Assert.True(layout.Rows[10].Height > layout.List.Height);
        Assert.Equal(layout.Rows[10].Y, scroll, 0.01);
        // A short row already in view stays where it is.
        Assert.Equal(0, new HistoryPanelLayout(Body, rows, opened: "k0").Reveal(0, 0));
    }

    [Fact]
    public void ARowSaysTheTurnsTheTimeTheModelAndTheCost()
    {
        var item = Talk("a", Morning) with { Turns = [new AiTurn("What\nis   this?", [], [], "x"), new AiTurn("More", [], [], "y")], CostUsd = 0 };
        var lines = HistoryPanelLayout.RowText(item, 532, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal("What is this?", lines[0].Text);
        Assert.StartsWith("2 turns · 14 Oct, ", lines[1].Text, StringComparison.Ordinal);
        Assert.EndsWith(" · m · no cost", lines[1].Text, StringComparison.Ordinal);
        Assert.All(Enum.GetValues<AiSource>(), source => Assert.NotEmpty(HistoryPanelLayout.Glyph(source)));
    }

    [Fact]
    public void ThePaletteFooterHasTheHistoryButtonBeforeWhereTheRequestGoes()
    {
        var layout = new PaletteLayout((float)PanelSize.PaletteWidth, mascot: true);
        float height = layout.Height(0);
        var button = layout.History(height);
        var foot = layout.Foot("Nothing sent", 0, 0, 2m, height);

        Assert.True(button.Width >= StateLook.MinTarget && button.Height >= StateLook.MinTarget);
        Assert.True(button.X >= 0 && button.Bottom <= height);
        Assert.True(foot[0].Slot.X >= button.Right);
        Assert.Equal(PaletteLayout.HistoryTip, layout.HistoryTarget(height).Tip);
        Assert.Contains("costs more", PaletteLayout.ContinueLine(null), StringComparison.Ordinal);
        Assert.StartsWith("Continuing \"Remind me", PaletteLayout.ContinueLine("Remind me what the dentist said"), StringComparison.Ordinal);
    }
}
