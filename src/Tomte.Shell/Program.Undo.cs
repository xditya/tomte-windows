using System.Numerics;
using Tomte.Core;
using Tomte.Core.Interaction;
using Tomte.Core.Triggers;
using Windows.Win32;

namespace Tomte.Shell;

/// <summary>The undo driver.</summary>
internal static partial class Program
{
    /// <summary>
    /// --debug-undo palette|search|zone|card with --debug-log=path and --debug-settings file (whose provider it sends to):
    /// closes each kind of AI view in the ways a user does and brings it back, with no input to the desktop. Hotkeys are
    /// posted to the island as the input thread posts them (Esc, the shortcut again, another view's shortcut, Win+Alt+Z),
    /// the pill's Undo is clicked by posting the island its own button messages, and a press outside as the input thread
    /// reports one. Logs what shows after each step, then quits. palette: a long answer scrolled up with a draft in the
    /// box, closed by Esc and brought back by a click; closed by its shortcut and brought back by Win+Alt+Z; a slow answer
    /// with the search opened in its place, then Esc, then Win+Alt+Z; a slow answer closed and left to run out. search: AI
    /// search closed by its shortcut, a click on Undo, then left to run out. zone: a drop on Summarize, closed by Esc,
    /// brought back, closed by a press outside. card: with --rambler, --debug-hold, --debug-transcript and
    /// --debug-choice esc, Undo after each Esc on the card (the hold driver quits). idle: an answer and an AI search, each
    /// closed and left to run out, then Tomte stays hidden to be measured. voice: with --rambler, --debug-hold,
    /// --debug-choice ask and a provider, Esc on the voice command's reply and Win+Alt+Z (the hold driver quits). events: an
    /// event raised with the AI box open and one raised while the undo pill shows, each before the pill, then a settings
    /// shortcut box starting to record. music: with "Keep showing while music plays" on and something playing, an answer
    /// closed and left to run out, then another closed, brought back by a click on Undo, closed again and left to run out.
    /// </summary>
    private static async Task DebugUndo(IslandWindow island, TriggerController triggers, SettingsStore store, string mode, Action<string> log)
    {
        // After the launch flight, which any open would cut short.
        await Task.Delay(2500);
        log($"debug undo: {mode}, undo after closing {store.Current.UndoSeconds} s");
        switch (mode)
        {
            case "palette":
                await Palette();
                break;
            case "search":
                await Search();
                break;
            case "zone":
                await Zone();
                break;
            case "card":
                await Card();
                return;
            case "idle":
                await Idle();
                return;
            case "voice":
                await Voice();
                return;
            case "events":
                await Events();
                break;
            case "music":
                await Music();
                break;
        }

        triggers.Hide();
        await Task.Delay(500);
        PInvoke.PostQuitMessage(0);

        async Task Palette()
        {
            Hotkey(HotkeyAction.Palette);
            await Until(() => hotkeyPalette?.View is { Session: not null } && triggers.Machine.State == IslandState.Palette);
            await Send("Write a long list");
            var view = hotkeyPalette!.View!;
            view.Command(NavKey.PageUp);
            view.Command(NavKey.PageUp);
            hotkeyPalette.Fill("a follow-up not sent yet");
            await Step("answered, scrolled up two pages, a draft typed");

            Post(InputThread.EscapeHotkeyId);
            await Step("Esc");
            Click();
            await Step("Undo clicked");

            Hotkey(HotkeyAction.Palette);
            await Step("the shortcut again");
            Post(InputThread.UndoHotkeyId);
            await Step("Win+Alt+Z");

            await Send("slow, please");
            await Step("a slow answer on its way");
            Hotkey(HotkeyAction.Search);
            await Step("the search opened in its place");
            Post(InputThread.EscapeHotkeyId);
            await Step("Esc on the search");
            Post(InputThread.UndoHotkeyId);
            await Step("Win+Alt+Z, with what arrived meanwhile");

            await Until(() => hotkeyPalette.View?.Session?.Status is not Tomte.AI.Palette.PaletteStatus.Sending, 30000);
            await Send("slow again");
            await Step("another slow answer on its way");
            Post(InputThread.EscapeHotkeyId);
            await Step("Esc");
            await Task.Delay(11000);
            await Step("the undo time ran out");
        }

        async Task Search()
        {
            await AskAiSearch();
            log($"debug undo: AI search ran, selected {search!.View!.Selected?.Kind} \"{search.View.Selected?.Name}\"");
            await Task.Delay(200);
            search.View.Press(0x28, ctrl: false, shift: false);
            await Step("AI search answered, second row selected");

            Hotkey(HotkeyAction.Search);
            await Step("the shortcut again");
            Click();
            await Step("Undo clicked");

            Hotkey(HotkeyAction.Search);
            await Step("the shortcut again");
            await Task.Delay(11000);
            await Step("the undo time ran out");
        }

        async Task Zone()
        {
            await Jobs.Shelf!.Load();
            var items = await Jobs.Shelf.Add([new ShelfEntry(ShelfKind.Content, "undo-zone-test.txt", Content: "Tomte undo test.\nA few lines for a summary.\n"u8.ToArray())]);
            PInvoke.PostMessage(island.Hwnd, InputThread.MsgDragInZone, 0, 0);
            await Until(() => triggers.Machine.State == IslandState.ShelfDrop);
            var run = zones!.DebugRun(DropZone.Summarize, items);
            await Task.WhenAny(run, Task.Delay(30000));
            await Step("the drop zone answered");

            Post(InputThread.EscapeHotkeyId);
            await Step("Esc");
            Click();
            await Step("Undo clicked");

            PInvoke.PostMessage(island.Hwnd, InputThread.MsgPressOutside, 0, 0);
            await Step("a press outside");
            await Task.Delay(11000);
            await Step("the undo time ran out");
        }

        // An answer and an AI search, each closed and left to run out; then Tomte stays, hidden, to be measured.
        async Task Idle()
        {
            Hotkey(HotkeyAction.Palette);
            await Until(() => hotkeyPalette?.View is { Session: not null } && triggers.Machine.State == IslandState.Palette);
            await Send("Write a long list");
            Post(InputThread.EscapeHotkeyId);
            await Task.Delay(11000);
            await AskAiSearch();
            Hotkey(HotkeyAction.Search);
            await Task.Delay(11000);
            await Step("both ran out; hidden from here on");
        }

        // Events and the undo pill: one raised while the AI box is open shows first at its close, then the pill for its time
        // left; one raised while the pill shows takes its place at once, then the pill comes back for the time left.
        async Task Events()
        {
            Hotkey(HotkeyAction.Palette);
            await Until(() => hotkeyPalette?.View is { Session: not null } && triggers.Machine.State == IslandState.Palette);
            await Send("Write a long list");
            triggers.Raise(new IslandEvent("Volume 30%.", DateTimeOffset.Now));
            await Step("an event raised with the AI box open");
            Post(InputThread.EscapeHotkeyId);
            await Step("Esc");
            await Task.Delay(3000);
            await Step("the event's time is up");
            Post(InputThread.UndoHotkeyId);
            await Step("Win+Alt+Z");

            Post(InputThread.EscapeHotkeyId);
            await Step("Esc");
            triggers.Raise(new IslandEvent("Timer set for 5:00.", DateTimeOffset.Now));
            await Step("an event raised while the pill shows");
            await Task.Delay(3000);
            await Step("the event's time is up");

            // A settings shortcut box starts recording, as the settings window posts it: the pill goes with Win+Alt+Z.
            PInvoke.PostMessage(island.Hwnd, Tomte.Core.Hotkey.MsgPause, 1, 0);
            await Step("a settings shortcut box records");
            PInvoke.PostMessage(island.Hwnd, Tomte.Core.Hotkey.MsgPause, 0, 0);
            await Task.Delay(5000);
            await Step("the undo time ran out");
        }

        // The undo pill comes before the pill kept while music plays, and the kept pill once the undo time is up.
        async Task Music()
        {
            var time = UndoClose.Time(store.Current.UndoSeconds) ?? TimeSpan.Zero;
            await Until(() => triggers.Compact == CompactReason.Playing, 20000);
            await Step("music plays");
            Hotkey(HotkeyAction.Palette);
            await Until(() => hotkeyPalette?.View is { Session: not null } && triggers.Machine.State == IslandState.Palette);
            await Send("Write a long list");
            Post(InputThread.EscapeHotkeyId);
            await Step("Esc");
            await Task.Delay(time);
            await Step("the undo time ran out");

            Hotkey(HotkeyAction.Palette);
            await Until(() => hotkeyPalette?.View is { Session: not null } && triggers.Machine.State == IslandState.Palette);
            await Send("Write a long list");
            Post(InputThread.EscapeHotkeyId);
            await Step("Esc again");
            Click();
            await Step("Undo clicked");
            Post(InputThread.EscapeHotkeyId);
            await Step("Esc after Undo");
            await Task.Delay(time);
            await Step("the undo time ran out again");
        }

        // With --rambler, --debug-hold, --debug-choice ask and a provider: Esc on the voice command's reply, then Win+Alt+Z.
        async Task Voice()
        {
            await Until(() => triggers.Machine.State == IslandState.Palette && !triggers.Searching, 20000);
            await Task.Delay(1500);
            await Step("the voice command's reply");
            Post(InputThread.EscapeHotkeyId);
            await Step("Esc");
            Post(InputThread.UndoHotkeyId);
            await Step("Win+Alt+Z");
        }

        async Task Card()
        {
            for (int i = 1; i <= 2; i++)
            {
                await Until(() => triggers.UndoShowing is not null, 20000);
                await Step($"Esc on the card ({i})");
                if (i == 1)
                {
                    Post(InputThread.UndoHotkeyId);
                    await Step("Win+Alt+Z");
                }
            }
        }

        // The search box with words no local row matches well, Search with AI chosen once Start's app list is in.
        async Task AskAiSearch()
        {
            Hotkey(HotkeyAction.Search);
            await Until(() => search?.View is not null);
            search!.Dry = true;
            await Task.Delay(2500);
            search.View!.Fill("find my undo files please");
            await Task.Delay(400);
            for (int i = 0; i < 6 && search.View.Selected?.Kind != Tomte.Core.Search.SearchKind.AiSearch; i++)
            {
                search.View.Press(0x09, ctrl: false, shift: false);
            }

            search.View.Press(0x0D, ctrl: false, shift: false);
            await Task.WhenAny(search.AiRun, Task.Delay(30000));
            await Task.Delay(200);
        }

        void Hotkey(HotkeyAction action) => Post((int)action + 1);

        void Post(int id) => PInvoke.PostMessage(island.Hwnd, InputThread.MsgHotkey, (nuint)id, 0);

        async Task Send(string words)
        {
            hotkeyPalette!.Fill(words);
            // The box reads its text on the next turn, and Send is a target once there is text.
            await Task.Delay(100);
            hotkeyPalette.View!.Activate(PaletteView.SendId);
            await Task.Delay(1500);
        }

        // A click on the pill's Undo, posted to the island as the mouse would send it.
        void Click()
        {
            if (triggers.UndoShowing?.Targets is not [var undo, ..])
            {
                log("debug undo: no undo pill to click");
                return;
            }

            var b = undo.Bounds;
            var at = island.IslandToScreen(new Vector2(b.X + (b.Width / 2), b.Y + (b.Height / 2)));
            PInvoke.ScreenToClient(island.Hwnd, ref at);
            nint point = (at.Y << 16) | (at.X & 0xFFFF);
            PInvoke.PostMessage(island.Hwnd, PInvoke.WM_LBUTTONDOWN, 1, point);
            PInvoke.PostMessage(island.Hwnd, PInvoke.WM_LBUTTONUP, 0, point);
        }

        async Task Step(string what)
        {
            await Task.Delay(1200);
            var view = hotkeyPalette?.View;
            string shows = triggers.Machine.State switch
            {
                IslandState.Compact when triggers.UndoShowing is { } pill => $"undo pill {pill.Width:F0} wide",
                IslandState.Palette when triggers.Searching && search?.View is { } s => $"search \"{s.Text}\", line \"{s.Lead}\", {s.Rows.Count} rows, selected \"{s.Selected?.Name}\"",
                IslandState.Palette when view is { Session: { } session } && !triggers.Searching => $"AI box: prompt \"{view.PromptText}\", scroll {view.Scrolled.Scroll:F0} pinned={view.Scrolled.Pinned}, "
                    + $"{session.Status}, answer {session.AnswerText.Length} chars, {session.Conversation.Count} turns",
                _ => "",
            };
            log($"debug undo: {what}: island {triggers.Machine.State}, compact {triggers.Compact}, tomte {island.Mascot.State}, {shows}");
            // Time to capture the screen.
            await Task.Delay(800);
        }

        static async Task Until(Func<bool> condition, int ms = 10000)
        {
            for (int waited = 0; waited < ms && !condition(); waited += 20)
            {
                await Task.Delay(20);
            }
        }
    }
}
