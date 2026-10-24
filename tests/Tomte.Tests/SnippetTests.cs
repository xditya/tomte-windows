using System.Drawing;
using System.Numerics;
using Tomte.Core.Storage;
using Tomte.Core.Text;
using Xunit;

namespace Tomte.Tests;

public sealed class SnippetTests : IDisposable
{
    private static readonly Snippet[] Saved =
    [
        new(1, "signature", "Best,\nAda"),
        new(2, "sigh", "*sigh*"),
        new(3, "Home address", "1 Main St\nSpringfield"),
        new(4, "addr work", "2 Office Rd", Used: 50),
    ];

    private readonly string dir = Path.Combine(Path.GetTempPath(), "tomte-snippets-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("/", 1, 0, "")]
    [InlineData("/sig", 4, 0, "sig")]
    [InlineData("hello /sig", 10, 6, "sig")]
    [InlineData("hello\t/addr", 11, 6, "addr")]
    [InlineData("one\n/x-y_z.1", 12, 4, "x-y_z.1")]
    [InlineData("say /sig more", 8, 4, "sig")]
    public void ASlashAtALineStartOrAfterASpaceOpensTheList(string text, int caret, int start, string query) =>
        Assert.Equal((start, query), SnippetSlash.Find(text, caret));

    [Theory]
    [InlineData("a/b", 3)]
    [InlineData("see https://example.com/sig", 27)]
    [InlineData("x /a/b", 6)]
    [InlineData("/signature", 4)]
    [InlineData("run `ls /sig", 12)]
    [InlineData("```\n/sig", 8)]
    [InlineData("plain", 5)]
    [InlineData("/sig!", 5)]
    [InlineData("", 0)]
    public void NotInAWordAnAddressCodeOrMidWord(string text, int caret) => Assert.Null(SnippetSlash.Find(text, caret));

    [Fact]
    public void AfterACodeSpanOrFenceClosesTheSlashWorksAgain()
    {
        Assert.Equal((9, "s"), SnippetSlash.Find("run `ls` /s", 11));
        Assert.Equal((13, "s"), SnippetSlash.Find("```\ncode\n```\n/s", 15));
    }

    [Fact]
    public void FilterRanksByNameWithTheMostRecentlyUsedFirstAmongEquals()
    {
        Assert.Equal(["sigh", "signature"], SnippetSlash.Filter(Saved, "sig").Select(s => s.Name));
        Assert.Equal(["addr work", "Home address"], SnippetSlash.Filter(Saved, "addr").Select(s => s.Name));
        Assert.Empty(SnippetSlash.Filter(Saved, "zzz"));

        // Nothing typed: the one used last first, then by name.
        Assert.Equal(["addr work", "Home address", "sigh", "signature"], SnippetSlash.Filter(Saved, "").Select(s => s.Name));

        // Equal scores: used last wins.
        Snippet[] twins = [new(1, "aa1", "x", Used: 1), new(2, "aa2", "y", Used: 9)];
        Assert.Equal(["aa2", "aa1"], SnippetSlash.Filter(twins, "aa").Select(s => s.Name));
        Assert.Equal(SnippetSlash.MaxRows, SnippetSlash.Filter([.. Enumerable.Range(0, 9).Select(i => new Snippet(i, $"s{i}", "t"))], "").Count);
    }

    [Fact]
    public void TheMenuKeepsItsChoiceWhileTheSameWordShowsAndCloses()
    {
        var menu = SnippetSlash.Menu("hi /sig", 7, Saved, null)!;
        Assert.Equal((3, "sig", 0), (menu.Start, menu.Query, menu.Selected));
        var down = menu.Move(1);
        Assert.Equal(1, down.Selected);
        Assert.Equal(0, down.Move(1).Selected);
        Assert.Equal(1, menu.Move(-1).Selected);
        Assert.Equal(1, SnippetSlash.Menu("hi /sig", 7, Saved, down)!.Selected);
        Assert.Equal(0, SnippetSlash.Menu("hi /sigh", 8, Saved, down)!.Selected);

        // No match with snippets saved: no list. None saved: the one row that says where to add them.
        Assert.Null(SnippetSlash.Menu("hi /zzz", 7, Saved, null));
        Assert.Empty(SnippetSlash.Menu("hi /zzz", 7, [], null)!.Rows);
        Assert.Null(SnippetSlash.Menu("a/b", 3, Saved, null));
    }

    [Fact]
    public void InsertingReplacesTheSlashWordAndItsReverseBringsTheWordBack()
    {
        const string text = "Thanks\n/sig";
        var menu = SnippetSlash.Menu(text, text.Length, Saved, null)!;
        var change = menu.Insert(menu.Rows[1]);
        string after = change.Apply(text);
        Assert.Equal("Thanks\nBest,\nAda", after);

        // The undo step is the same replacement the other way round.
        var back = new Splice(change.Start, change.Text.Length, text[change.Start..change.End]);
        Assert.Equal(text, back.Apply(after));
        Assert.Equal("a\nb", menu.Insert(new Snippet(9, "crlf", "a\r\nb")).Text);
    }

    [Fact]
    public void StoreRoundTrip()
    {
        Directory.CreateDirectory(dir);
        var store = new SnippetStore(new TomteDb(Path.Combine(dir, "tomte.db")));
        Assert.Empty(store.Load());
        long sig = store.Add("  sig\nnature ", "Best,\r\nAda");
        long addr = store.Add("addr", "1 Main St");
        Assert.Equal([("addr", "1 Main St", 0L), ("sig nature", "Best,\nAda", 0L)], store.Load().Select(s => (s.Name, s.Text, s.Used)));

        store.Update(sig, "signature", "Cheers");
        store.Used(addr, new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero));
        var loaded = store.Load();
        Assert.Equal(("signature", "Cheers"), (loaded[1].Name, loaded[1].Text));
        Assert.Equal(new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero).UtcTicks, loaded[0].Used);

        store.Delete(addr);
        Assert.Equal([sig], store.Load().Select(s => s.Id));
        Assert.Equal(SnippetStore.MaxName, SnippetStore.Name(new string('a', 100)).Length);
    }

    [Fact]
    public void TheListSitsUnderTheLineInsideTheCardOrAboveWhenThereIsMoreRoom()
    {
        var card = new RectangleF(16, 50, 488, 220);
        var below = new SnippetMenuLayout(card, new Vector2(100, 70), 20, 3);
        Assert.Equal(70 + 20 + SnippetMenuLayout.Gap, below.Box.Y);
        Assert.Equal((3 * SnippetMenuLayout.RowHeight) + (2 * SnippetMenuLayout.Pad), below.Box.Height);
        Assert.Equal(SnippetMenuLayout.Width, below.Box.Width);
        Assert.Equal(below.Box.Y + SnippetMenuLayout.Pad + SnippetMenuLayout.RowHeight, below.Row(1).Y);

        // Near the card's foot: above the line.
        var above = new SnippetMenuLayout(card, new Vector2(100, 240), 20, 3);
        Assert.Equal(240 - SnippetMenuLayout.Gap - above.Box.Height, above.Box.Y);

        // Near its right edge: pulled in, never past it.
        var right = new SnippetMenuLayout(card, new Vector2(490, 70), 20, 1);
        Assert.True(right.Box.Right <= card.Right - SnippetMenuLayout.Inset);
        Assert.True(right.Box.X >= card.X + SnippetMenuLayout.Inset);

        // The hint is one row; a row's name and text stay inside it, the text after the name.
        var hint = new SnippetMenuLayout(card, new Vector2(100, 70), 20, 0);
        Assert.Equal(1, hint.Count);
        Assert.Equal(SnippetMenuLayout.Hint, hint.HintSlot().Text);
        Assert.True(hint.Row(0).Contains(hint.HintSlot().Slot));
    }
}
