using Tomte.AI.Palette;
using Tomte.Core;

namespace Tomte.Shell;

/// <summary>
/// Pick a colour from the screen, from the pick to its card: the hex is copied and the island opens in the Palette state
/// on a drop result card with a swatch and the colour as HEX, RGB and HSL, each with its own Copy. Nothing leaves the PC.
/// The card holds the island until a choice, Close or Esc. On the island's thread.
/// </summary>
internal sealed class ColourRun
{
    private readonly TriggerController triggers;
    private readonly DropResultView view;
    private readonly PickedColour colour;
    private readonly Action<string>? log;
    private DropResult card;

    private ColourRun(IslandWindow island, TriggerController triggers, ShelfStore shelf, TextRenderer text, PickedColour colour, Action<string>? log)
    {
        (this.triggers, this.colour, this.log) = (triggers, colour, log);
        view = new DropResultView(island, triggers, shelf, text, log);
        view.Chose += Chose;
        card = colour.Card(Copy(colour.Hex));
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

    // Each Copy puts its format on the clipboard and closes the card; Close just closes it.
    private void Chose(int i)
    {
        if (i < card.Choices.Count && colour.Copies(card.Choices[i]) is { } words && !Copy(words))
        {
            card = card with { Line = ScreenText.ClipboardBusy };
            Show();
            return;
        }

        triggers.Hide();
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
