namespace Tomte.Core;

/// <summary>
/// The icon set, in a 24 x 24 box. Each entry is a list of figures: stroked ones are drawn <see cref="StrokeWidth"/>
/// wide with round caps and joins, filled ones are solid. Draw one with <c>PaletteView.DrawGlyph</c>.
/// </summary>
public static class Glyphs
{
    public const float Box = 24, StrokeWidth = 1.75f;

    private static GlyphFigure[] Line(params string[] paths) => [.. paths.SelectMany(p => SvgPath.Parse(p, stroked: true))];

    private static GlyphFigure[] Solid(params string[] paths) => [.. paths.SelectMany(p => SvgPath.Parse(p, stroked: false))];

    private static string Rect(float x, float y, float w, float h, float r) => SvgPath.Rect(x, y, w, h, r);

    private static string Dot(float x, float y, float r) => SvgPath.Circle(x, y, r);

    public static readonly GlyphFigure[] Home = Line("M3 10.5 12 3l9 7.5V20a1 1 0 0 1-1 1h-5v-6h-6v6H4a1 1 0 0 1-1-1z");
    public static readonly GlyphFigure[] Shelf = Line("M3 13h5l1.5 2.5h5L16 13h5", "M5 5h14l2 8v6a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1v-6z");
    public static readonly GlyphFigure[] Shortcuts = Line(Rect(3, 3, 7, 7, 1.5f), Rect(14, 3, 7, 7, 1.5f), Rect(3, 14, 7, 7, 1.5f), Rect(14, 14, 7, 7, 1.5f));
    public static readonly GlyphFigure[] Media = Line("M9 18V5l11-2v13", Dot(6, 18, 3), Dot(17, 16, 3));
    public static readonly GlyphFigure[] Scratchpad = Line("M4 20h4L19 9l-4-4L4 16z", "M14 6l4 4");

    /// <summary>Same drawing as <see cref="Scratchpad"/>.</summary>
    public static readonly GlyphFigure[] Pencil = Scratchpad;
    public static readonly GlyphFigure[] Clipboard = Line(Rect(5, 4, 14, 17, 2), "M9 4V3h6v1M9 10h6M9 14h4");
    public static readonly GlyphFigure[] History = Line(Dot(12, 12, 9), "M12 7v5l3 2");
    public static readonly GlyphFigure[] Search = Line(Dot(11, 11, 7), "m20 20-4-4");
    public static readonly GlyphFigure[] Settings = Line("M4 6h10M18 6h2M4 12h4M12 12h8M4 18h12", Dot(16, 6, 2), Dot(10, 12, 2), Dot(18, 18, 2));
    public static readonly GlyphFigure[] Sparkle = Solid("M12 3c.6 4.6 2.4 6.4 7 7-4.6.6-6.4 2.4-7 7-.6-4.6-2.4-6.4-7-7 4.6-.6 6.4-2.4 7-7z");
    public static readonly GlyphFigure[] Mic = Line(Rect(9, 3, 6, 11, 3), "M5 11a7 7 0 0 0 14 0M12 18v3");
    public static readonly GlyphFigure[] Region = Line("M4 8V5a1 1 0 0 1 1-1h3M16 4h3a1 1 0 0 1 1 1v3M20 16v3a1 1 0 0 1-1 1h-3M8 20H5a1 1 0 0 1-1-1v-3");
    public static readonly GlyphFigure[] Window = Line(Rect(3, 4, 18, 16, 2), "M3 9h18");
    public static readonly GlyphFigure[] Link = Line("M10 14a4 4 0 0 0 5.7 0l3-3a4 4 0 0 0-5.7-5.7l-1 1", "M14 10a4 4 0 0 0-5.7 0l-3 3a4 4 0 0 0 5.7 5.7l1-1");
    public static readonly GlyphFigure[] Text = Line("M4 6h16M4 12h16M4 18h10");
    public static readonly GlyphFigure[] Folder = Line("M3 6a1 1 0 0 1 1-1h5l2 2h9a1 1 0 0 1 1 1v10a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1z");
    public static readonly GlyphFigure[] Lock = Line(Rect(5, 11, 14, 10, 2), "M8 11V7a4 4 0 0 1 8 0v4");
    public static readonly GlyphFigure[] Pin = Line("M12 17v4M8 3h8l-1 6 3 3v2H6v-2l3-3z");
    public static readonly GlyphFigure[] Undo = Line("M9 14 4 9l5-5", "M4 9h11a5 5 0 0 1 0 10h-3");
    public static readonly GlyphFigure[] Send = Line("M12 19V5M5 12l7-7 7 7");
    public static readonly GlyphFigure[] Close = Line("M6 6l12 12M18 6 6 18");
    public static readonly GlyphFigure[] Plus = Line("M12 5v14M5 12h14");
    public static readonly GlyphFigure[] Check = Line("m5 12 5 5 9-10");
    public static readonly GlyphFigure[] Ask = Line("M4 5h16v11H9l-5 4z");
    public static readonly GlyphFigure[] Summarize = Line("M4 6h16M4 12h11M4 18h7");
    public static readonly GlyphFigure[] Table = Line(Rect(3, 4, 18, 16, 2), "M3 10h18M3 15h18M9 4v16");
    public static readonly GlyphFigure[] Rename = Line("M4 7h10M4 12h7M4 17h10", "M18 5v14M16 5h4M16 19h4");
    public static readonly GlyphFigure[] Timer = Line(Dot(12, 13, 8), "M12 9v4l2 2M9 2h6");
    public static readonly GlyphFigure[] Terminal = Line(Rect(3, 4, 18, 16, 2), "m7 9 3 3-3 3M13 15h4");
    public static readonly GlyphFigure[] Globe = Line(Dot(12, 12, 9), "M3 12h18M12 3a14 14 0 0 1 0 18M12 3a14 14 0 0 0 0 18");
    public static readonly GlyphFigure[] Play = Solid("M8 5v14l11-7z");
    public static readonly GlyphFigure[] Pause = Solid(Rect(6.5f, 5, 4, 14, 1), Rect(13.5f, 5, 4, 14, 1));

    /// <summary>A bar stroked and a head filled.</summary>
    public static readonly GlyphFigure[] Prev = [.. Line("M6 5v14"), .. Solid("M18 5 9 12l9 7z")];
    public static readonly GlyphFigure[] Next = [.. Line("M18 5v14"), .. Solid("M6 5l9 7-9 7z")];

    public static readonly GlyphFigure[] Printer = Line("M6 9V3h12v6M6 18H4v-7a2 2 0 0 1 2-2h12a2 2 0 0 1 2 2v7h-2", Rect(6, 14, 12, 7, 0));
    public static readonly GlyphFigure[] ChevronDown = Line("m6 9 6 6 6-6");
    public static readonly GlyphFigure[] ArrowUp = Line("M12 20V9M8 13l4-4 4 4");
    public static readonly GlyphFigure[] Minus = Line("M5 12h14");
    public static readonly GlyphFigure[] File = Line("M14 3H6a1 1 0 0 0-1 1v16a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1V8z", "M14 3v5h5M9 13h6M9 17h4");
    public static readonly GlyphFigure[] Cursor = Line("M5 3l14 8-6 1.5L10 19z");
    public static readonly GlyphFigure[] Wrench = Line("M14.7 6.3a4 4 0 0 0-5.4 5.4L3 18l3 3 6.3-6.3a4 4 0 0 0 5.4-5.4l-2.6 2.6-2.4-.6-.6-2.4z");
    public static readonly GlyphFigure[] Info = Line(Dot(12, 12, 9), "M12 11v5M12 8h.01");

    /// <summary>Every glyph by name, for tests and lookups by key.</summary>
    public static readonly IReadOnlyDictionary<string, GlyphFigure[]> All = new Dictionary<string, GlyphFigure[]>
    {
        [nameof(Home)] = Home,
        [nameof(Shelf)] = Shelf,
        [nameof(Shortcuts)] = Shortcuts,
        [nameof(Media)] = Media,
        [nameof(Scratchpad)] = Scratchpad,
        [nameof(Pencil)] = Pencil,
        [nameof(Clipboard)] = Clipboard,
        [nameof(History)] = History,
        [nameof(Search)] = Search,
        [nameof(Settings)] = Settings,
        [nameof(Sparkle)] = Sparkle,
        [nameof(Mic)] = Mic,
        [nameof(Region)] = Region,
        [nameof(Window)] = Window,
        [nameof(Link)] = Link,
        [nameof(Text)] = Text,
        [nameof(Folder)] = Folder,
        [nameof(Lock)] = Lock,
        [nameof(Pin)] = Pin,
        [nameof(Undo)] = Undo,
        [nameof(Send)] = Send,
        [nameof(Close)] = Close,
        [nameof(Plus)] = Plus,
        [nameof(Check)] = Check,
        [nameof(Ask)] = Ask,
        [nameof(Summarize)] = Summarize,
        [nameof(Table)] = Table,
        [nameof(Rename)] = Rename,
        [nameof(Timer)] = Timer,
        [nameof(Terminal)] = Terminal,
        [nameof(Globe)] = Globe,
        [nameof(Play)] = Play,
        [nameof(Pause)] = Pause,
        [nameof(Prev)] = Prev,
        [nameof(Next)] = Next,
        [nameof(Printer)] = Printer,
        [nameof(ChevronDown)] = ChevronDown,
        [nameof(ArrowUp)] = ArrowUp,
        [nameof(Minus)] = Minus,
        [nameof(File)] = File,
        [nameof(Cursor)] = Cursor,
        [nameof(Wrench)] = Wrench,
        [nameof(Info)] = Info,
    };
}
