using System.Numerics;
using Tomte.Core;
using Xunit;

namespace Tomte.Tests;

public sealed class SvgPathTests
{
    private static OutlineSegment[] One(string d) => Assert.Single(SvgPath.Parse(d, stroked: true)).Path;

    [Fact]
    public void AbsoluteAndRelativeLinesHorizontalsAndVerticals()
    {
        var p = One("M1 2L4 6l2 1H10h-1V0v3");
        Assert.Equal(new Vector2(1, 2), p[0].End);
        Assert.Equal(new Vector2(4, 6), p[1].End);
        Assert.Equal(new Vector2(6, 7), p[2].End);
        Assert.Equal(new Vector2(10, 7), p[3].End);
        Assert.Equal(new Vector2(9, 7), p[4].End);
        Assert.Equal(new Vector2(9, 0), p[5].End);
        Assert.Equal(new Vector2(9, 3), p[6].End);
    }

    [Fact]
    public void ImplicitRepeatsAndSignOrDotSeparatedNumbers()
    {
        var p = One("M3 10.5 12 3l9 7.5");
        Assert.Equal(new Vector2(12, 3), p[1].End);
        Assert.Equal(new Vector2(21, 10.5f), p[2].End);
        var q = One("m.6 4.6-1 2 3-1");
        Assert.Equal(new Vector2(.6f, 4.6f), q[0].End);
        Assert.Equal(-.4f, q[1].End.X, 4);
        Assert.Equal(2.6f, q[2].End.X, 4);
    }

    [Fact]
    public void CubicSmoothCubicReflectsTheLastControl()
    {
        var p = One("M0 0C1 0 2 1 2 2S3 4 4 4");
        Assert.Equal(OutlineSegmentKind.Cubic, p[1].Kind);
        Assert.Equal(new Vector2(2, 3), p[2].Control1);
        Assert.Equal(new Vector2(4, 4), p[2].End);
    }

    [Fact]
    public void ClosePathStartsNextSubpathAndMarksFigureClosed()
    {
        var f = SvgPath.Parse("M0 0h4v4zm1 1h2", stroked: false);
        Assert.Equal(2, f.Length);
        Assert.False(f[0].Open);
        Assert.True(f[1].Open);
        Assert.Equal(new Vector2(1, 1), f[1].Path[0].End);
        Assert.False(f[0].Stroked);
    }

    [Fact]
    public void ArcsBecomeQuarterTurnCubicsThatEndWhereAsked()
    {
        // Half circle of radius 5, then a relative quarter.
        var p = One("M0 0A5 5 0 0 1 10 0a5 5 0 0 1 5 5");
        Assert.All(p.Skip(1), s => Assert.Equal(OutlineSegmentKind.Cubic, s.Kind));
        Assert.Equal(3, p.Length - 1);
        Assert.Equal(10, p[2].End.X, 3);
        Assert.Equal(0, p[2].End.Y, 3);
        Assert.Equal(new Vector2(15, 5), p[3].End);
        // Sweep 1 from (0,0) to (10,0) bulges up (negative y): the first quarter ends at the top of the circle.
        Assert.Equal(5, p[1].End.X, 3);
        Assert.Equal(-5, p[1].End.Y, 3);
    }

    [Fact]
    public void RoundedRectAndCircleHelpersCloseOnTheirBox()
    {
        var rect = One(SvgPath.Rect(5, 4, 14, 17, 2));
        Assert.Equal(5, rect.Min(s => s.End.X), 3);
        Assert.Equal(19, rect.Max(s => s.End.X), 3);
        Assert.Equal(21, rect.Max(s => s.End.Y), 3);
        var circle = One(SvgPath.Circle(6, 18, 3));
        Assert.Equal(3, circle.Min(s => s.End.X), 3);
        Assert.Equal(9, circle.Max(s => s.End.X), 3);
    }

    [Fact]
    public void BadDataThrows()
    {
        Assert.Throws<FormatException>(() => SvgPath.Parse("M1", true));
        Assert.Throws<FormatException>(() => SvgPath.Parse("1 2", true));
        Assert.Throws<FormatException>(() => SvgPath.Parse("M0 0Q1 1 2 2", true));
    }

    [Fact]
    public void EveryGlyphParsesInsideTheBox()
    {
        foreach (var (name, figures) in Glyphs.All)
        {
            Assert.True(figures.Length > 0, name);
            foreach (var s in figures.SelectMany(f => f.Path))
            {
                Assert.InRange(s.End.X, -0.01f, Glyphs.Box + 0.01f);
                Assert.InRange(s.End.Y, -0.01f, Glyphs.Box + 0.01f);
            }
        }
    }

    [Fact]
    public void FilledGlyphsAreFilledAndTheRestStroked()
    {
        Assert.All(Glyphs.Sparkle, f => Assert.False(f.Stroked));
        Assert.All(Glyphs.Play, f => Assert.False(f.Stroked));
        Assert.All(Glyphs.Pause, f => Assert.False(f.Stroked));
        Assert.All(Glyphs.Home, f => Assert.True(f.Stroked));
        Assert.Equal([true, false], Glyphs.Prev.Select(f => f.Stroked));
    }
}
