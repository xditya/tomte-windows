using Tomte.AI.Palette;
using Tomte.Core;

namespace Tomte.Shell;

/// <summary>
/// Pick a colour from the screen, from the pick to its card: the hex is copied and the island opens in the Palette state
/// on a drop result card with the colour as a square beside its hex, RGB and HSL, each row copying itself with the usual
/// "Copied" at the row. Nothing leaves the PC. The card holds the island until Close or Esc. On the island's thread.
/// </summary>
internal sealed class ColourRun
{
    private readonly IslandWindow island;
    private readonly TriggerController triggers;
    private readonly DropResultView view;
    private readonly PickedColour colour;
    private readonly Action<string>? log;
    private DropResult card;

    private ColourRun(IslandWindow island, TriggerController triggers, ShelfStore shelf, TextRenderer text, PickedColour colour, Action<string>? log)
    {
        (this.island, this.triggers, this.colour, this.log) = (island, triggers, colour, log);
        view = new DropResultView(island, triggers, shelf, text, log);
        view.Chose += _ => triggers.Hide();
        view.ValueChosen += CopyValue;
        card = colour.Card(Copy(colour.Hex) ? colour.Hex : null);
        Show();
        // After the change to the Palette state, which seats the tomte by the prompt box: this card's row is its head.
        island.Seat = DropResultLayout.Seat;
        island.Mascot.Set(MascotState.Result);
    }

    /// <summary>The pixel is picked; the island is still in the Capture state.</summary>
    public static Task StartAsync(IslandWindow island, TriggerController triggers, Action<string>? log, CaptureOverlay overlay)
    {
        if (overlay.Picked is not { } colour || triggers.Machine.State != IslandState.Capture || Program.Text is not { } text || Jobs.Shelf is not { } shelf)
        {
            return Task.CompletedTask;
        }

        log?.Invoke($"colour picked: {colour.Hex}");
        triggers.PaletteFromCapture();
        _ = new ColourRun(island, triggers, shelf, text, colour, log);
        return Task.CompletedTask;
    }

    private void Show()
    {
        view.Show(card, []);
        triggers.ZonePalette(view.Height, busy: false);
    }

    // A value row puts its text on the clipboard and the card stays, so another format can follow; Close is the only choice.
    private void CopyValue(int i)
    {
        string id = DropResultLayout.ValuePrefix + i;
        if (i >= card.Values.Count || !Copy(card.Values[i]))
        {
            island.Interaction.Acknowledge(view, id, "Another app has the clipboard open", tick: false);
            return;
        }

        card = colour.Card(card.Values[i]);
        Show();
        island.Interaction.Acknowledge(view, id);
    }

    private bool Copy(string words)
    {
        try
        {
            DesktopEffects.CopyToClipboard(words);
            return true;
        }
        catch (InvalidOperationException e)
        {
            // Another app kept the clipboard open.
            log?.Invoke($"colour picked: not copied ({e.Message})");
            return false;
        }
    }
}
