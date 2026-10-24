using System.Drawing;
using System.Numerics;
using Tomte.Core;
using Tomte.Core.Device;
using Tomte.Core.Interaction;
using Tomte.Core.Triggers;
using Xunit;

namespace Tomte.Tests;

public class CallControlsTests
{
    private const uint Own = 4242;

    private static readonly CallApp Teams = CallApp.Identify(1200, "ms-teams", "Microsoft Teams");

    private static CallState State(bool appSound = true, bool ended = false, string? failure = null) =>
        new() { App = Teams, Mic = "Headset Microphone (Jabra)", Level = 0.64f, AppSound = appSound, Speaker = "Speakers (Realtek Audio)", Ended = ended, Failure = failure };

    [Theory]
    [InlineData("ms-teams", "Microsoft Teams", "Teams", "Ctrl+Shift+O")]
    [InlineData("Teams", null, "Teams", "Ctrl+Shift+O")]
    [InlineData("Zoom", "Zoom Workplace", "Zoom", "Alt+V")]
    [InlineData("zoom", null, "Zoom", "Alt+V")]
    [InlineData("Discord", "Discord", "Discord", null)]
    [InlineData("slack", "Slack", "Slack", null)]
    // A browser's camera keys would reach whichever tab is in front: none.
    [InlineData("chrome", "Google Chrome", "Google Chrome", null)]
    [InlineData("obs64", "", "Obs64", null)]
    public void CallAppsAreKnownByTheirProcessAndOnlyDesktopOnesHaveCameraKeys(string process, string? description, string name, string? keys)
    {
        var app = CallApp.Identify(7, process, description);
        Assert.Equal((name, keys), (app.Name, app.CameraKeys));
    }

    [Fact]
    public void TheCallerIsTheFirstOtherAppRecording()
    {
        Assert.Equal(3400u, MicUseWatch.Caller([(Own, true), (1200, false), (3400, true), (5600, true)], Own));
        Assert.Equal(0u, MicUseWatch.Caller([(Own, true), (0, true), (1200, false)], Own));
    }

    [Fact]
    public void TheCallsVolumeIsItsOwnProcessesSessionThenAnotherOfItsName()
    {
        // Its own process wins, playing or not, over another process of its name.
        Assert.Equal(2, Teams.PickSession([(900, "ms-teams", true), (5, "spotify", true), (1200, "ms-teams", false)]));
        Assert.Equal(1, Teams.PickSession([(1200, "ms-teams", false), (1200, "ms-teams", true)]));
        // A browser records in one process and plays in another: the playing one of its name.
        Assert.Equal(1, Teams.PickSession([(900, "MS-TEAMS", false), (901, "ms-teams", true)]));
        // Nothing of the app's, nor a session shared by several processes: the speaker's own volume.
        Assert.Equal(-1, Teams.PickSession([(5, "spotify", true), (0, "", true)]));
        Assert.Equal(-1, Teams.PickSession([]));
    }

    [Fact]
    public void TheCameraIsTheCallsWhenItsAppStreamsAndAnothersOtherwise()
    {
        Assert.Equal((CameraUse.Call, 0u), Teams.Camera([(1200, "ms-teams")], Own));
        Assert.Equal((CameraUse.Call, 0u), Teams.Camera([(77, "NVIDIA Broadcast"), (901, "ms-teams")], Own));
        Assert.Equal((CameraUse.Other, 77u), Teams.Camera([(77, "NVIDIA Broadcast"), (78, "obs64")], Own));
        Assert.Equal((CameraUse.Off, 0u), Teams.Camera([(Own, "Tomte.Shell")], Own));
        Assert.Equal((CameraUse.Off, 0u), Teams.Camera([], Own));
    }

    [Fact]
    public void DevicesAreListedByNameWithTheCurrentOneMarked()
    {
        var ordered = CallPanelLayout.Order(
        [
            new("c", "Speakers (Realtek Audio)", true),
            new("a", "headphones (WH-1000XM4)", false),
            new("b", "Headset (Jabra)", false),
        ]);
        Assert.Equal(["a", "b", "c"], ordered.Select(d => d.Id));
        Assert.True(ordered[2].IsDefault);
    }

    [Theory]
    [InlineData(760)]
    [InlineData(614)]
    [InlineData(480)]
    public void TheCallViewFitsItsBodyAtEveryPanelWidth(float width)
    {
        var layout = new CallPanelLayout(new Vector2(width, CallPanelLayout.Height));
        var body = new RectangleF(0, 0, width, CallPanelLayout.Height);
        var targets = layout.Targets(State());
        Assert.Equal(
            [CallPanelLayout.MicId, CallPanelLayout.MicDeviceId, CallPanelLayout.SoundId, CallPanelLayout.VolumeId, CallPanelLayout.SpeakerDeviceId, CallPanelLayout.SwitchId],
            targets.Select(t => t.Id));
        Assert.All(targets, t => Assert.True(body.Contains(t.Bounds), t.Id));
        // The cards side by side, the row and the line under them, none overlapping.
        Assert.False(layout.MicCard.IntersectsWith(layout.SoundCard));
        Assert.True(layout.Row.Top > layout.SoundCard.Bottom && layout.Foot.Top > layout.Row.Bottom);
        Assert.True(body.Contains(layout.Foot));
        // Inside each card the button, the words and the device capsule.
        foreach (var card in new[] { layout.MicCard, layout.SoundCard })
        {
            Assert.True(card.Contains(CallPanelLayout.DiscIn(card)) && card.Contains(CallPanelLayout.DeviceIn(card)) && card.Contains(CallPanelLayout.StatusIn(card)));
            Assert.True(CallPanelLayout.DeviceIn(card).Top >= CallPanelLayout.DiscIn(card).Bottom);
        }

        // The bar has room, and the percentage sits after it; the camera's words end before the clock and the button.
        Assert.True(layout.Bar.Width >= 80, $"bar {layout.Bar.Width}");
        Assert.True(layout.Percent.Left > layout.Bar.Right);
        Assert.True(layout.CameraLine.Width >= 120 && layout.CameraLine.Right < layout.Clock.Left && layout.Clock.Right < layout.Switch.Left);
        Assert.True(layout.Row.Contains(layout.Switch));
    }

    [Theory]
    [InlineData(760, 2)]
    [InlineData(614, 2)]
    [InlineData(480, 1)]
    public void TheDeviceListTakesTheBodyInOneOrTwoColumns(float width, int columns)
    {
        var layout = new CallPanelLayout(new Vector2(width, CallPanelLayout.Height));
        Assert.Equal(columns, layout.Columns);
        Assert.True(layout.Fits >= 3 * columns);
        var last = layout.ListRowAt(layout.Fits - 1);
        Assert.True(last.Bottom <= layout.Foot.Top && last.Right <= width - CallPanelLayout.Pad + 0.01);
        Assert.True(layout.Title.Width > 100 && layout.Title.Right < layout.Settings.Left);

        AudioDevice[] devices = [.. Enumerable.Range(0, 12).Select(i => new AudioDevice($"{i}", $"Mic {i:00}", i == 2))];
        var targets = layout.ListTargets(devices, 0);
        Assert.Equal([CallPanelLayout.BackId, CallPanelLayout.SettingsId], targets.Take(2).Select(t => t.Id));
        Assert.Equal(layout.Fits, targets.Count - 2);
        Assert.Contains(targets, t => t.Id == CallPanelLayout.DeviceId(2) && t.Checked);
        // The wheel scrolls a line at a time and never past the end.
        Assert.Equal(columns, layout.Scrolled(0, 1, devices.Length));
        Assert.Equal(0, layout.Scrolled(columns, -5, devices.Length));
        int end = layout.Scrolled(0, 99, devices.Length);
        Assert.True(end + layout.Fits >= devices.Length && end + layout.Fits < devices.Length + layout.Fits);
        Assert.Equal(0, layout.Scrolled(0, 3, 2));
    }

    [Fact]
    public void TheVolumeBarMapsToLevelsAndStepsStayInRange()
    {
        var layout = new CallPanelLayout(new Vector2(760, CallPanelLayout.Height));
        Assert.Equal(0, layout.LevelAt(layout.Bar.Left - 20));
        Assert.Equal(1, layout.LevelAt(layout.Bar.Right + 20));
        Assert.Equal(0.25f, layout.LevelAt(layout.BarX(0.25f)), 3);
        Assert.Equal(0.69f, CallPanelLayout.Stepped(0.64f, 1, CallPanelLayout.KeyStep), 3);
        Assert.Equal(1f, CallPanelLayout.Stepped(0.98f, 2, CallPanelLayout.KeyStep));
        Assert.Equal(0f, CallPanelLayout.Stepped(0.01f, -1, CallPanelLayout.WheelStep));
        Assert.Equal("64%", CallPanelLayout.PercentText(0.64f));
    }

    [Fact]
    public void TheViewSaysWhatItControlsAndWhatChanged()
    {
        Assert.Equal("TEAMS VOLUME", CallPanelLayout.SoundOverline(State()));
        Assert.Equal("SPEAKER VOLUME", CallPanelLayout.SoundOverline(State(appSound: false)));
        Assert.Equal("Mute Teams", CallPanelLayout.SoundTip(State()));
        Assert.Equal("Muted for every app", CallPanelLayout.MicStatus(true));
        Assert.Equal("Switch to the call, then Ctrl+Shift+O turns it on or off", CallPanelLayout.CameraHintText(Teams));
        Assert.Equal("Turn it on or off in Discord", CallPanelLayout.CameraHintText(CallApp.Identify(1, "Discord", null)));
        Assert.Equal("Camera in use by NVIDIA Broadcast", CallPanelLayout.CameraText(CameraUse.Other, "NVIDIA Broadcast"));
        Assert.Equal(CallPanelLayout.Follow, CallPanelLayout.FootText(State()));
        Assert.Equal("Teams stopped using the default microphone", CallPanelLayout.FootText(State(ended: true)));
        Assert.Equal("Windows did not switch to Headset (0x80070005). Sound settings can", CallPanelLayout.SwitchFailed("Headset", unchecked((int)0x80070005)));
        Assert.Equal("Windows did not switch to Headset (0x80070005). Sound settings can", CallPanelLayout.FootText(State(failure: CallPanelLayout.SwitchFailed("Headset", unchecked((int)0x80070005)))));
        Assert.DoesNotContain("\u2014", CallPanelLayout.Follow);
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(65, "1:05")]
    [InlineData(3723, "1:02:03")]
    [InlineData(-3, "0:00")]
    public void TheCallsClockReadsMinutesThenHours(int seconds, string shown) =>
        Assert.Equal(shown, CallPanelLayout.ClockText(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void TheCallTabShowsOnlyDuringACallAndGivesBackTheTabBefore()
    {
        var tabs = new PanelTabs();
        foreach (var id in Enum.GetValues<PanelId>())
        {
            tabs.Register(id);
        }

        // No job near, so the Jobs tab is off the strip too.
        tabs.Show(PanelId.Jobs, false);
        tabs.Show(PanelId.Call, false);
        Assert.DoesNotContain(PanelId.Call, tabs.Order);
        Assert.False(tabs.Select(PanelId.Call));

        tabs.Show(PanelId.Call, true);
        Assert.Equal(PanelId.Call, tabs.Order.Last());
        Assert.True(tabs.Select(PanelId.Call));

        // The call ends with its tab current: the first tab is, and Step goes round the tabs that show.
        tabs.Show(PanelId.Call, false);
        Assert.Equal(PanelId.Home, tabs.Current);
        tabs.Select(PanelId.History);
        Assert.True(tabs.Step(1));
        Assert.Equal(PanelId.Home, tabs.Current);
    }

    [Fact]
    public void AClickOnThePillDuringACallOpensTheCallTab()
    {
        Assert.Equal(PanelId.Call, CompactRules.ClickOpens(nowPlayingShows: true, onCall: true));
        Assert.Equal(PanelId.Media, CompactRules.ClickOpens(nowPlayingShows: true, onCall: false));
        Assert.Null(CompactRules.ClickOpens(nowPlayingShows: false, onCall: false));
        // The tab before the click comes back when the island hides.
        Assert.Equal(PanelId.Shelf, CompactRules.TabAfterClick(PanelId.Shelf, PanelId.Call));
    }

    [Fact]
    public void TheCallPanelIsTheBodyUnderTheTabRow()
    {
        Assert.Equal((760, PanelSpace.Head + CallPanelLayout.Height), PanelSize.Of(PanelId.Call));
        Assert.Equal(TargetKind.Slider, new CallPanelLayout(new Vector2(760, CallPanelLayout.Height)).Targets(State()).Single(t => t.Id == CallPanelLayout.VolumeId).Kind);
    }
}
