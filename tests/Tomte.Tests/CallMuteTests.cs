using Tomte.Core;
using Tomte.Core.Device;
using Tomte.Core.Triggers;
using Xunit;

namespace Tomte.Tests;

public class CallMuteTests
{
    private const uint Own = 4242;

    [Fact]
    public void AnotherAppRecordingIsACall()
    {
        // Teams records, a browser tab has its session open but idle.
        Assert.True(MicUseWatch.OthersCapture([(1200, true), (3400, false)], Own));
        Assert.False(MicUseWatch.OthersCapture([(1200, false), (3400, false)], Own));
        Assert.False(MicUseWatch.OthersCapture([], Own));
    }

    [Fact]
    public void TomteItselfIsNeverACall()
    {
        // The rambler records from Tomte's own process; a session with no single process behind it is not an app.
        Assert.False(MicUseWatch.OthersCapture([(Own, true), (0, true)], Own));
        Assert.True(MicUseWatch.OthersCapture([(Own, true), (1200, true)], Own));
    }

    [Fact]
    public void NvidiaBroadcastIsNeverACall()
    {
        Assert.True(MicUseWatch.AlwaysOn("NVIDIA Broadcast"));
        Assert.True(MicUseWatch.AlwaysOn("NVIDIA Broadcast UI"));
        Assert.True(MicUseWatch.AlwaysOn("NvBroadcast.Container"));
        Assert.False(MicUseWatch.AlwaysOn("ms-teams"));
        Assert.False(MicUseWatch.AlwaysOn("Discord"));
    }

    [Theory]
    // In a call the push pill and the kept pill carry the button; without one they do not.
    [InlineData(CompactReason.Summary, IslandEventSource.Other, true, false, true)]
    [InlineData(CompactReason.Playing, IslandEventSource.Other, true, false, true)]
    [InlineData(CompactReason.Summary, IslandEventSource.Other, false, false, false)]
    [InlineData(CompactReason.Playing, IslandEventSource.Other, false, false, false)]
    // Desktop peek's pill rests in a shape of its own.
    [InlineData(CompactReason.Summary, IslandEventSource.Other, true, true, false)]
    [InlineData(CompactReason.Peek, IslandEventSource.Other, true, true, false)]
    // The mute key's pill shows the button, call or not; another feature's event and the other pills do not.
    [InlineData(CompactReason.Event, IslandEventSource.Mic, false, false, true)]
    [InlineData(CompactReason.Event, IslandEventSource.Timer, true, false, false)]
    [InlineData(CompactReason.Event, IslandEventSource.Other, true, false, false)]
    [InlineData(CompactReason.Undo, IslandEventSource.Other, true, false, false)]
    [InlineData(CompactReason.Rambler, IslandEventSource.Other, true, false, false)]
    [InlineData(CompactReason.Hint, IslandEventSource.Other, true, false, false)]
    [InlineData(CompactReason.Arrival, IslandEventSource.Other, true, false, false)]
    [InlineData(CompactReason.None, IslandEventSource.Other, true, false, false)]
    public void TheButtonShowsOnTheRightPills(CompactReason reason, IslandEventSource shown, bool call, bool peek, bool expected)
    {
        Assert.Equal(expected, CompactRules.ShowsMic(reason, shown, call, peek));
    }

    [Fact]
    public void TheMuteKeysPillSaysTheState()
    {
        Assert.Equal("Microphone muted", CompactRules.MicLine(muted: true));
        Assert.Equal("Microphone on", CompactRules.MicLine(muted: false));
        Assert.Equal("Unmute the microphone", CompactLayout.MicTip(muted: true));
        Assert.Equal("Mute the microphone", CompactLayout.MicTip(muted: false));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheButtonTakesTheRoomThePillGrowsByAndNothingElse(bool mascot)
    {
        // The pill grows by MicRoom; its content keeps its 220 wide frame at the leading end, so in the grown pill's
        // island space everything laid out for the 220 pill moves left by half the room.
        float shift = CompactLayout.MicRoom / 2, width = (float)IslandGeometry.Compact.Width + CompactLayout.MicRoom;
        var button = CompactLayout.MicButton;
        Assert.Equal(width / 2 - CompactLayout.UndoInset, button.Right, 3);
        Assert.Equal((float)IslandGeometry.Compact.Depth / 2, button.Y + button.Height / 2, 3);

        // Words that end the pill, a ring or art with its words, and the now-playing row's bars all stop a gap short of it.
        Assert.True(CompactLayout.Line(mascot).Right - shift + CompactLayout.Gap <= button.X);
        var (_, x, w) = CompactLayout.Group(mascot, CompactLayout.RingSize, 500);
        Assert.True(x + w - shift + CompactLayout.Gap <= button.X);
        float bars = CompactLayout.BarsLeft + (4 * Tomte.Core.Rambler.Waveform.BarWidth) + (3 * Tomte.Core.Rambler.Waveform.Gap);
        Assert.True(bars - shift + CompactLayout.Gap <= button.X);

        // And the room is no bigger than that gap needs, rounded up to a whole DIP.
        Assert.True(CompactLayout.Line(mascot).Right - shift + CompactLayout.Gap + 1 > button.X);
    }

    [Fact]
    public void TheMuteKeyIsWinAltUAndFreeAmongTomtesOwn()
    {
        var keys = new HotkeySettings();
        Assert.Equal("Win+Alt+U", keys.Mute);
        Assert.Equal("Win+Alt+U", keys[HotkeyAction.Mute]);
        Assert.Equal("Mute the microphone", Hotkey.Name(HotkeyAction.Mute));
        Assert.Equal(HotkeyAction.Mute, Enum.GetValues<HotkeyAction>()[^1]);
        Assert.Equal(Enum.GetValues<HotkeyAction>().Length, Enum.GetValues<HotkeyAction>().Select(a => keys[a]).Distinct().Count());
        Assert.Null(Hotkey.Validate(keys, HotkeyAction.Mute, keys.Mute));
        Assert.Equal("Win+Alt+K", keys.With(HotkeyAction.Mute, "Win+Alt+K").Mute);
    }

    [Fact]
    public void TheMuteKeyConflictsLikeTheOthers()
    {
        Assert.Equal("Mute the microphone already uses Win+Alt+U.", Hotkey.Validate(new HotkeySettings(), HotkeyAction.Summon, "Alt+Win+U"));
        Assert.Equal("Scratchpad already uses Win+Alt+N.", Hotkey.Validate(new HotkeySettings(), HotkeyAction.Mute, "Win+Alt+N"));
        Assert.Contains("brings back a closed AI answer", Hotkey.Validate(new HotkeySettings(), HotkeyAction.Mute, "Win+Alt+Z"));
        Assert.Contains("Add Win", Hotkey.Validate(new HotkeySettings(), HotkeyAction.Mute, "Ctrl+Alt+U"));
        Assert.Null(Hotkey.Validate(new HotkeySettings(), HotkeyAction.Mute, ""));
    }

    [Fact]
    public void TheButtonIsOnByDefault() => Assert.True(new TomteSettings().MuteDuringCalls);
}
