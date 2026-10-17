namespace Tomte.Core;

/// <summary>
/// Items laid out left to right in lines no wider than the space there is, as a wrap panel does (WinUI has none): the
/// next item starts a new line when it would not fit on this one. An item wider than a line still gets a line of its own.
/// </summary>
public static class WrapLayout
{
    /// <summary>
    /// The top-left corner of each item, in order, and the width and height they take together. <paramref name="spacing"/>
    /// is between items on a line, <paramref name="lineSpacing"/> between lines.
    /// </summary>
    public static (IReadOnlyList<(double X, double Y)> At, double Width, double Height) Place(IReadOnlyList<(double Width, double Height)> items, double width, double spacing, double lineSpacing)
    {
        var at = new List<(double X, double Y)>(items.Count);
        double x = 0, y = 0, line = 0, widest = 0;
        foreach (var (w, h) in items)
        {
            if (x > 0 && x + w > width)
            {
                (x, y, line) = (0, y + line + lineSpacing, 0);
            }

            at.Add((x, y));
            widest = Math.Max(widest, x + w);
            line = Math.Max(line, h);
            x += w + spacing;
        }

        return (at, widest, y + line);
    }
}
