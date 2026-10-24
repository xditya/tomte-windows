using Tomte.Core;
using Xunit;

namespace Tomte.Tests;

public sealed class ColourPickTests
{
    [Theory]
    [InlineData(0, 0, 0, "#000000", "rgb(0, 0, 0)", "hsl(0, 0%, 0%)")]
    [InlineData(255, 255, 255, "#FFFFFF", "rgb(255, 255, 255)", "hsl(0, 0%, 100%)")]
    [InlineData(255, 0, 0, "#FF0000", "rgb(255, 0, 0)", "hsl(0, 100%, 50%)")]
    [InlineData(0, 128, 0, "#008000", "rgb(0, 128, 0)", "hsl(120, 100%, 25%)")]
    [InlineData(58, 123, 213, "#3A7BD5", "rgb(58, 123, 213)", "hsl(215, 65%, 53%)")]
    [InlineData(255, 0, 128, "#FF0080", "rgb(255, 0, 128)", "hsl(330, 100%, 50%)")]
    [InlineData(128, 128, 128, "#808080", "rgb(128, 128, 128)", "hsl(0, 0%, 50%)")]
    public void AColourIsWrittenAsHexRgbAndHsl(byte r, byte g, byte b, string hex, string rgb, string hsl)
    {
        var colour = new PickedColour(r, g, b);
        Assert.Equal((hex, rgb, hsl), (colour.Hex, colour.RgbText, colour.HslText));
        Assert.Equal(uint.Parse(hex[1..], System.Globalization.NumberStyles.HexNumber), colour.Rgb);
    }

    [Fact]
    public void APixelIsReadFromBgra()
    {
        // Two pixels wide, two tall: the bottom-right one is #3A7BD5.
        byte[] frame = [0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255, 213, 123, 58, 255];
        Assert.Equal(new PickedColour(58, 123, 213), PickedColour.At(frame, 2, 1, 1));
    }

    [Fact]
    public void TheCardHasASwatchAndACopyForEachFormat()
    {
        var card = new PickedColour(58, 123, 213).Card(copied: true);
        Assert.Equal(0x3A7BD5u, card.Swatch);
        Assert.Equal("Copied #3A7BD5 · picked on this PC", card.Line);
        Assert.Equal([PickedColour.CopyHex, PickedColour.CopyRgb, PickedColour.CopyHsl, DropResult.Close], card.Choices);
        Assert.Equal(["#3A7BD5", "rgb(58, 123, 213)", "hsl(215, 65%, 53%)", null], card.Choices.Select(new PickedColour(58, 123, 213).Copies));

        // The swatch sits above the values, which sit above the choices.
        var layout = new DropResultLayout(card, (float)PanelSize.PaletteWidth, mascot: true);
        Assert.Equal(DropResultLayout.SwatchHeight, layout.SwatchBand.Height);
        Assert.True(layout.SwatchBand.Bottom <= layout.Band.Top);
        Assert.True(layout.Band.Bottom <= layout.Choices[0].Y);
        Assert.InRange(layout.Height, PanelSize.PaletteMinHeight, PanelSize.PaletteMaxHeight);
        Assert.True(new DropResultLayout(ScreenTextCard, (float)PanelSize.PaletteWidth, mascot: true).SwatchBand.IsEmpty);
    }

    private static DropResult ScreenTextCard => new("t", "i", "l", false, "body", [], [DropResult.Close]);

    [Theory]
    [InlineData(1.0, 10)]
    [InlineData(1.25, 13)]
    [InlineData(1.5, 15)]
    [InlineData(2.0, 20)]
    public void TheLoupeKeepsWholePixelBlocksAtEveryScale(double scale, int cell)
    {
        Assert.Equal(cell, Loupe.Cell(scale));
        var (zoom, label) = Loupe.Place(100, 100, 3840, 2160, scale);
        Assert.Equal(Loupe.Cells * cell, zoom.Width);
        Assert.Equal(zoom.Width, zoom.Height);
        // Below and right of the pointer, the label under the square.
        int offset = (int)Math.Round(Loupe.OffsetDips * scale);
        Assert.Equal((100 + offset, 100 + offset), (zoom.X, zoom.Y));
        Assert.Equal((zoom.X, zoom.Width), (label.X, label.Width));
        Assert.True(label.Y > zoom.Y + zoom.Height);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void TheLoupeFlipsAtTheEdgesAndStaysOnTheMonitor(double scale)
    {
        const int Width = 1920, Height = 1080;
        var (zoom, label) = Loupe.Place(Width - 5, Height - 5, Width, Height, scale);
        Assert.True(zoom.X + zoom.Width < Width - 5);
        Assert.True(label.Y + label.Height < Height - 5);

        foreach (var (x, y) in new[] { (0, 0), (Width - 1, 0), (0, Height - 1), (Width / 2, Height / 2) })
        {
            (zoom, label) = Loupe.Place(x, y, Width, Height, scale);
            Assert.InRange(zoom.X, 0, Width - zoom.Width);
            Assert.InRange(label.Y + label.Height, 0, Height);
        }
    }

    [Fact]
    public void TheCentreCellIsThePointersPixelAndTheEdgesClamp()
    {
        Assert.Equal((500, 300), Loupe.Source(500, 300, Loupe.Cells / 2, Loupe.Cells / 2, 1920, 1080));
        Assert.Equal((495, 295), Loupe.Source(500, 300, 0, 0, 1920, 1080));
        Assert.Equal((505, 305), Loupe.Source(500, 300, Loupe.Cells - 1, Loupe.Cells - 1, 1920, 1080));
        Assert.Equal((0, 0), Loupe.Source(2, 1, 0, 0, 1920, 1080));
        Assert.Equal((1919, 1079), Loupe.Source(1918, 1078, Loupe.Cells - 1, Loupe.Cells - 1, 1920, 1080));
    }

    [Fact]
    public void PickAColourComesBeforeMuteWithAFreeDefault()
    {
        Assert.Equal(HotkeyAction.ColourPick, Enum.GetValues<HotkeyAction>()[^2]);
        Assert.Equal("Pick a colour from the screen", Hotkey.Name(HotkeyAction.ColourPick));
        var bindings = new HotkeySettings();
        Assert.Equal("Win+Alt+E", bindings[HotkeyAction.ColourPick]);
        // No other Tomte action and none of the chords Tomte keeps for itself use it.
        foreach (var action in Enum.GetValues<HotkeyAction>())
        {
            Assert.True(action == HotkeyAction.ColourPick || bindings[action] != bindings.ColourPick);
        }

        Assert.Null(Hotkey.Validate(bindings, HotkeyAction.ColourPick, bindings.ColourPick));
        Assert.Equal("Pick a colour from the screen already uses Win+Alt+E.", Hotkey.Validate(bindings, HotkeyAction.TextCapture, "Win+Alt+E"));
        Assert.Equal("Win+Alt+Shift+E", bindings.With(HotkeyAction.ColourPick, "Win+Alt+Shift+E")[HotkeyAction.ColourPick]);
    }
}
