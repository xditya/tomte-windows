using System.Numerics;
using Tomte.AI;
using Tomte.AI.Palette;
using Tomte.Core;
using Tomte.Core.Device;
using Tomte.Core.Interaction;
using Tomte.Core.Text;
using Xunit;

namespace Tomte.Tests;

/// <summary>
/// A tool's question in the typed palette: the session asks through a <see cref="ToolQuestion"/>, the palette draws
/// it above the footer, and Run, Cancel, Always allow, Enter or Esc answer it. Scripted provider turns only.
/// </summary>
public sealed class PaletteQuestionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"tomte-question-{Guid.NewGuid():N}");
    private readonly ScriptedProvider _provider = new();
    private readonly List<string> _opened = [], _copied = [];
    private readonly PermissionGrants _grants = new();
    private readonly ToolQuestion _question;
    private long _now = 10_000;

    public PaletteQuestionTests() => _question = new ToolQuestion { Clock = () => _now };

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private PaletteSession Session()
    {
        var config = new ProviderConfig("p", ProviderKind.OpenAICompatible, "http://localhost", "m");
        var tools = new BuiltInTools(new InMemoryShelf(), new InMemoryScratchpad(), _copied.Add, _opened.Add);
        return new PaletteSession(_provider, config, new PaletteOptions(), new SendLog(Path.Combine(_dir, "send-log.jsonl")), tools, _grants, [])
        {
            Prompt = "do it",
            ConfirmTool = _question.AskAsync,
        };
    }

    // Completes with the request once the question is open, and the user has read it: the clock moves past the wait.
    private Task<ToolRequest> Opened(ToolRequest? after = null)
    {
        var opened = new TaskCompletionSource<ToolRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed()
        {
            if (_question.Asked is { } asked && !ReferenceEquals(asked, after))
            {
                _question.Changed -= Changed;
                _now += ToolQuestion.ReadMs;
                opened.TrySetResult(asked);
            }
        }

        _question.Changed += Changed;
        Changed();
        return opened.Task;
    }

    [Fact]
    public async Task RunRunsWhatWasAskedOnceAndGrantsNothing()
    {
        _provider.Turns.Add(ScriptedProvider.Call(BuiltInTools.OpenUrl, """{"url":"https://example.com/"}"""));
        _provider.Turns.Add(ScriptedProvider.Text(deltas: "Opened."));
        var session = Session();

        var send = session.SendAsync();
        var asked = await Opened();
        Assert.Equal("Open https://example.com/", asked.Summary);
        Assert.Equal(ActionTier.ExternalEffect, asked.Tier);
        Assert.True(asked.OfferAlwaysAllow);
        Assert.Empty(_opened);

        Assert.True(_question.Answer(PermissionAnswer.AllowOnce, asked));
        await send;
        Assert.Equal(["https://example.com/"], _opened);
        Assert.Null(_question.Asked);
        Assert.Empty(_grants.AlwaysAllowed);
        Assert.Equal(PaletteStatus.Done, session.Status);
    }

    [Fact]
    public async Task AlwaysAllowRunsItAndTheNextCallDoesNotAsk()
    {
        _provider.Turns.Add(ScriptedProvider.Call(BuiltInTools.CopyToClipboard, """{"text":"hello"}"""));
        _provider.Turns.Add(ScriptedProvider.Text(deltas: "Copied."));
        var first = Session();
        var send = first.SendAsync();
        Assert.True(_question.Answer(PermissionAnswer.AlwaysAllow, await Opened()));
        await send;
        Assert.Equal(["hello"], _copied);
        Assert.Contains(BuiltInTools.CopyToClipboard, _grants.AlwaysAllowed);

        // The next palette's call runs at once: nothing opens the question.
        int opened = 0;
        _question.Changed += () => opened++;
        var second = Session();
        _provider.Requests.Clear();
        await second.SendAsync();
        Assert.Equal(0, opened);
        Assert.Equal(["hello", "hello"], _copied);
    }

    [Fact]
    public async Task CancelRunsNothingAndTellsTheModel()
    {
        _provider.Turns.Add(ScriptedProvider.Call(BuiltInTools.OpenUrl, """{"url":"https://example.com/"}"""));
        _provider.Turns.Add(ScriptedProvider.Text(deltas: "Fine."));
        var session = Session();
        var send = session.SendAsync();
        Assert.True(_question.Answer(PermissionAnswer.Deny, await Opened()));
        await send;
        Assert.Empty(_opened);
        var step = Assert.Single(session.Answer.OfType<ToolStepSegment>());
        Assert.True(step.IsError);
        Assert.Equal(PaletteSession.NotAllowed, step.Result);
        Assert.Equal(PaletteStatus.Done, session.Status);
    }

    [Fact]
    public async Task ClosingThePaletteWhileItAsksAnswersNoAndEndsTheSend()
    {
        _provider.Turns.Add(ScriptedProvider.Call(BuiltInTools.OpenUrl, """{"url":"https://example.com/"}"""));
        _provider.Turns.Add(ScriptedProvider.Text(deltas: "Fine."));
        var session = Session();
        var send = session.SendAsync();
        await Opened();
        session.Cancel();
        await send;
        Assert.Empty(_opened);
        Assert.Null(_question.Asked);
        Assert.Equal(PaletteStatus.Cancelled, session.Status);
    }

    [Fact]
    public async Task TheQuestionSaysWhenItOpensAndClosesAndAlwaysAllowCountsOnceWhereNotOffered()
    {
        int changes = 0;
        _question.Changed += () => changes++;
        // Nothing open: Enter sends and Esc hides, because nothing took them.
        Assert.False(_question.Answer(PermissionAnswer.AllowOnce));

        var request = new ToolRequest(BuiltInTools.ShutDownPc, "Shut down the PC", ActionTier.Destructive, OfferAlwaysAllow: false);
        var asked = _question.AskAsync(request, CancellationToken.None);
        Assert.Equal(1, changes);
        _now += ToolQuestion.ReadMs;
        Assert.True(_question.Answer(PermissionAnswer.AlwaysAllow, request));
        Assert.Equal(PermissionAnswer.AllowOnce, await asked);
        Assert.Equal(2, changes);
        Assert.False(_question.Answer(PermissionAnswer.Deny, request));
    }

    [Fact]
    public async Task AYesBeforeTheQuestionHasShownForReadMsIsNotTakenAndANoAlwaysIs()
    {
        var power = new ToolRequest(BuiltInTools.ShutDownPc, "Shut down the PC", ActionTier.Destructive, OfferAlwaysAllow: false);
        var asked = _question.AskAsync(power, CancellationToken.None);
        // A second Enter, a double click or a second chord a moment after the last question was answered.
        _now += ToolQuestion.ReadMs - 1;
        Assert.False(_question.Answer(PermissionAnswer.AllowOnce, power));
        Assert.False(_question.Answer(PermissionAnswer.AlwaysAllow, power));
        Assert.Same(power, _question.Asked);
        _now += 1;
        Assert.True(_question.Answer(PermissionAnswer.AllowOnce, power));
        Assert.Equal(PermissionAnswer.AllowOnce, await asked);

        // Cancel and Esc count at once.
        var again = _question.AskAsync(power with { }, CancellationToken.None);
        Assert.True(_question.Answer(PermissionAnswer.Deny, _question.Asked));
        Assert.Equal(PermissionAnswer.Deny, await again);
    }

    [Fact]
    public async Task ARunForTheCardThatWasShownNeverRunsTheNextOne()
    {
        // Two calls in one turn: the second question opens as soon as the first tool has run.
        _provider.Turns.Add([.. ScriptedProvider.Call(BuiltInTools.OpenUrl, """{"url":"https://example.com/a"}""", "c1").SkipLast(1),
            .. ScriptedProvider.Call(BuiltInTools.OpenUrl, """{"url":"https://example.com/b"}""", "c2")]);
        _provider.Turns.Add(ScriptedProvider.Text(deltas: "Done."));
        var session = Session();
        var send = session.SendAsync();
        var first = await Opened();
        Assert.True(_question.Answer(PermissionAnswer.AllowOnce, first));
        var second = await Opened(after: first);
        Assert.NotSame(first, second);
        // The same press again, still meant for the first card, reaches nothing; the second card is answered on its own.
        Assert.False(_question.Answer(PermissionAnswer.AllowOnce, first));
        Assert.True(_question.Answer(PermissionAnswer.Deny, second));
        await send;
        Assert.Equal(["https://example.com/a"], _opened);
    }

    [Fact]
    public void AHeldEnterSendsOrRunsOnceAndItsRepeatsAreSwallowed()
    {
        var keys = new PromptKeys();
        Assert.True(keys.Sends(PromptKeys.KeyDown, 0x0D, shift: false, out bool swallow));
        Assert.True(swallow);
        Assert.False(keys.Sends(PromptKeys.Char, '\r', shift: false, out swallow));
        Assert.True(swallow);
        // Autorepeat (lParam bit 30): neither sent nor typed, while the card that just opened waits for a real press.
        Assert.False(keys.Sends(PromptKeys.KeyDown, 0x0D, shift: false, out swallow, PromptKeys.WasDown | 1));
        Assert.True(swallow);
        Assert.False(keys.Sends(PromptKeys.Char, '\r', shift: false, out swallow, PromptKeys.WasDown | 1));
        Assert.True(swallow);
    }

    [Fact]
    public void TheLineOnlyOffersEnterWhileThePromptHasTheKeys()
    {
        Assert.Equal("Reaches outside Tomte · Enter runs it, Esc cancels", Confirmation.LineFor(ActionTier.ExternalEffect, ConfirmKeys.Prompt));
        Assert.Equal("Reaches outside Tomte · Esc cancels", Confirmation.LineFor(ActionTier.ExternalEffect, ConfirmKeys.Pointer));
        Assert.Equal("Changes Tomte's own data · Win+Alt+Enter runs it, Esc cancels", Confirmation.LineFor(ActionTier.AppLocalWrite, ConfirmKeys.Voice));
    }

    [Fact]
    public void TheQuestionSitsAboveTheFooterWithRunLastAndAlwaysAllowOnlyWhereOffered()
    {
        var plain = new PaletteLayout(640, mascot: true);
        var confirm = new Confirmation("Turn dark mode on", Confirmation.LineFor(ActionTier.ExternalEffect, ConfirmKeys.Prompt), AlwaysAllow: true, Destructive: false);
        var layout = new PaletteLayout(640, mascot: true, confirm: confirm);
        Assert.Null(plain.Ask(150));

        // The card takes its own room: the palette grows by it, it starts under the chips while there is no answer,
        // and an answer's band ends a gap above it.
        Assert.True(layout.Height(0) > plain.Height(0));
        Assert.Equal(layout.AnswerTop, layout.Ask(layout.Height(0))!.Value.Card.Y, 1);
        float height = layout.Height(100);
        Assert.Equal(plain.Height(100) + layout.Ask(height)!.Value.Card.Height + PaletteLayout.Gap, height, 1);
        var ask = layout.Ask(height)!.Value;
        Assert.Equal(height - PaletteLayout.Bottom - PaletteLayout.FootHeight - PaletteLayout.Gap, ask.Card.Bottom, 1);
        Assert.Equal(PaletteLayout.Pad, ask.Card.X);
        Assert.Equal(100, layout.AnswerBand(height).Height, 1);
        Assert.Equal(layout.AnswerBand(height).Bottom, ask.Card.Y - PaletteLayout.Gap, 1);

        // Always allow, Cancel, Run from left to right; Run against the card's right padding; the line before them.
        Assert.Equal([ConfirmButton.AlwaysAllow, ConfirmButton.Cancel, ConfirmButton.Run], ask.Buttons.Select(b => b.Button));
        Assert.Equal(ask.Card.Right - PaletteLayout.AskPad, ask.Buttons[^1].Box.Right, 1);
        Assert.All(ask.Buttons, b => Assert.True(b.Box.Width >= PaletteLayout.ButtonMinWidth));
        Assert.True(ask.Line.Slot.Right <= ask.Buttons[0].Box.X);
        Assert.Equal("Turn dark mode on", ask.Summary.Text);
        Assert.Equal("Reaches outside Tomte · Enter runs it, Esc cancels", ask.Line.Text);

        // The buttons are the host's targets: big enough, inside the card, and none over another.
        Assert.All(ask.Buttons, b => Assert.True(b.Box.Height >= StateLook.MinTarget && ask.Card.Contains(b.Box)));
        Assert.True(ask.Buttons[0].Box.Right < ask.Buttons[1].Box.X && ask.Buttons[1].Box.Right < ask.Buttons[2].Box.X);

        // A tier that never offers it shows Cancel and Run only; by voice the keys are the chord.
        var power = new PaletteLayout(640, mascot: true, confirm: confirm with { AlwaysAllow = false, Line = Confirmation.LineFor(ActionTier.Destructive, ConfirmKeys.Voice) });
        var asked = power.Ask(power.Height(0))!.Value;
        Assert.Equal([ConfirmButton.Cancel, ConfirmButton.Run], asked.Buttons.Select(b => b.Button));
        Assert.Equal("Asks every time · Win+Alt+Enter runs it, Esc cancels", asked.Line.Text);
    }
}
