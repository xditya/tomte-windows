using System.Runtime.InteropServices;
using Tomte.Core.Text;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Controls.RichEdit;

namespace Tomte.Shell;

/// <summary>
/// The note's Markdown in its text box: each run <see cref="NoteMarkdown.Style"/> hands out gets the character format
/// of its look and each line its hanging indent, through the services' text object model, so the selection and the
/// caret stay on the characters they were on. Undo is suspended meanwhile, so Ctrl+Z only ever undoes text.
/// Dispose before the box. Island thread only.
/// </summary>
internal sealed unsafe class NoteStyle : IDisposable
{
    // COLORREF, 0x00BBGGRR, as they read over the note's card: white at about 60% for markers and done tasks, the
    // palette's info blue for links, code a little grey on white at about 8%.
    private const int White = 0xFFFFFF, Dim = 0xA3A3A3, LinkColor = 0xFFA86F, CodeColor = 0xDED6D6, CodeBack = 0x2C2A2A;
    private const string Mono = "Consolas";

    // The box's paragraphs stop a tab every half inch.
    private const float TabDips = 48;
    private const float PointsPerDip = 0.75f;

    private readonly TextServices services;
    private readonly ITextDocument* document;
    private readonly ITextRange* range;
    private readonly float size;
    private readonly Dictionary<MarkdownLook, nint> fonts = [];
    private readonly Dictionary<string, float> hangs = [];
    private bool disposed;

    public NoteStyle(TextServices services)
    {
        this.services = services;
        size = services.FontDips;
        document = services.Document();
        ITextRange* at;
        document->Range(0, 0, &at);
        range = at;
    }

    /// <summary>The zoom the indents were last set for: the services zoom the text but not the indents, so a new zoom wants them all again.</summary>
    public float Zoom { get; private set; } = 1;

    /// <summary>
    /// Styles the lines from <paramref name="from"/> (a line's start) to <paramref name="to"/> (a line's end) of
    /// <paramref name="text"/>, the box's text.
    /// </summary>
    public void Apply(string text, int from, int to)
    {
        if (services.Zoom != Zoom)
        {
            Zoom = services.Zoom;
            hangs.Clear();
        }

        var (lines, runs) = NoteMarkdown.Style(text, from, to);
        // Frozen, the services lay the lines out again once at the end, not after every format.
        document->Undo((int)tomConstants.tomSuspend, null);
        document->Freeze(null);
        try
        {
            // All of it plain first, one call for the characters and one for the lines, then what looks otherwise.
            range->SetRange(from, to + 1);
            range->SetFont(Font(MarkdownLook.Plain));
            Indent(0);
            foreach (var run in runs)
            {
                if (run.Look != MarkdownLook.Plain)
                {
                    range->SetRange(run.Start, run.Start + run.Length);
                    range->SetFont(Font(run.Look));
                }
            }

            foreach (var line in lines)
            {
                if (line.Hang > 0)
                {
                    range->SetRange(line.Start, line.Start);
                    Indent(Hang(text.Substring(line.Start, line.Hang)) * PointsPerDip * Zoom);
                }
            }
        }
        finally
        {
            document->Unfreeze(null);
            document->Undo((int)tomConstants.tomResume, null);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        foreach (nint font in fonts.Values)
        {
            ((ITextFont*)font)->Release();
        }

        fonts.Clear();
        range->Release();
        document->Release();
    }

    // One font object per look, made once from the box's own default and kept: a font applied whole is one call.
    private ITextFont* Font(MarkdownLook look)
    {
        if (fonts.TryGetValue(look, out nint kept))
        {
            return (ITextFont*)kept;
        }

        ITextFont* live, font;
        range->GetFont(&live);
        try
        {
            live->GetDuplicate(&font);
        }
        finally
        {
            live->Release();
        }

        try
        {
            const MarkdownLook headings = MarkdownLook.Heading1 | MarkdownLook.Heading2 | MarkdownLook.Heading3 | MarkdownLook.Heading4;
            bool code = (look & MarkdownLook.Code) != 0;
            font->Reset(tomConstants.tomDefault);
            nint name = Marshal.StringToBSTR(code ? Mono : TextStyle.Ui);
            try
            {
                font->SetName(new BSTR((char*)name));
            }
            finally
            {
                Marshal.FreeBSTR(name);
            }

            float dips = (look & MarkdownLook.Heading1) != 0 ? 18 : (look & MarkdownLook.Heading2) != 0 ? 16 : (look & MarkdownLook.Heading3) != 0 ? 14.5f : code ? size - 1 : size;
            font->SetSize(dips * PointsPerDip);
            font->SetWeight((look & MarkdownLook.Bold) != 0 ? 700 : (look & headings) != 0 ? 600 : 400);
            font->SetItalic(Flag((look & MarkdownLook.Italic) != 0));
            font->SetStrikeThrough(Flag((look & (MarkdownLook.Strike | MarkdownLook.Done)) != 0));
            font->SetUnderline((int)tomConstants.tomNone);
            font->SetForeColor((look & (MarkdownLook.Marker | MarkdownLook.Done)) != 0 ? Dim : (look & MarkdownLook.Link) != 0 ? LinkColor : code ? CodeColor : White);
            font->SetBackColor(code ? CodeBack : (int)tomConstants.tomAutoColor);
        }
        catch
        {
            font->Release();
            throw;
        }

        fonts[look] = (nint)font;
        return font;
    }

    private static int Flag(bool on) => (int)(on ? tomConstants.tomTrue : tomConstants.tomFalse);

    // The range's lines: the first starts at the margin, the lines it wraps onto this many points in.
    private void Indent(float hang)
    {
        ITextPara* para;
        range->GetPara(&para);
        try
        {
            para->SetIndents(-hang, hang, 0);
        }
        finally
        {
            para->Release();
        }
    }

    // How far an item's text starts from its line's start, in DIPs of the text: a tab to the next stop (not zoomed
    // either), the rest measured in the box's font. A measure leaves out white space at the end, so each piece is
    // measured with a letter after it.
    private float Hang(string hang)
    {
        if (hangs.TryGetValue(hang, out float width))
        {
            return width;
        }

        var style = new TextStyle(size);
        float letter = TextSlot.Measure("x", style, 10_000), tab = TabDips / Zoom;
        var pieces = hang.Split('\t');
        for (int i = 0; i < pieces.Length; i++)
        {
            width += pieces[i].Length == 0 ? 0 : TextSlot.Measure(pieces[i] + "x", style, 10_000) - letter;
            if (i < pieces.Length - 1)
            {
                width = (MathF.Floor(width / tab) + 1) * tab;
            }
        }

        hangs[hang] = width;
        return width;
    }
}
