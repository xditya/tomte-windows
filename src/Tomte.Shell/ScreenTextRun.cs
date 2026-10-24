using System.Runtime.CompilerServices;
using Tomte.AI.Palette;
using Tomte.Core;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Tomte.Shell;

/// <summary>
/// Copy text from screen, from the rectangle to its card: the region is taken, the island opens in the Palette state on a
/// drop result card that says it is reading, and the text Windows reads there is copied and shown with Copy and Close.
/// With the cleanup on in Settings and a provider set up, the text alone then goes to that provider, and the card shows
/// the cleaned text, marked as AI's, copied in its place, with the text as read one click away. With a provider set up
/// the card also offers Translate, which sends the text shown, and only on that click, for a translation into the
/// language picked in Settings, shown and copied the same way. Without a provider it offers Translate in browser
/// instead, which opens Google Translate with the text shown, and only on that click. A QR code in the region takes the text's place: its
/// content is copied and shown whole, and a web link gets Open link, which is the only way it opens. The card holds the
/// island until Close or Esc; once something was sent, Undo can bring it back for a while, as for a drop zone's result.
/// On the island's thread.
/// </summary>
internal sealed class ScreenTextRun(IslandWindow island, TriggerController triggers, ShelfStore shelf, TextRenderer text, SettingsStore store, Action<string>? log)
{
    /// <summary>What the undo pill says after the card closed.</summary>
    internal const string TextClosed = "Text closed";

    private DropResultView view = null!;
    private DropResult last = ScreenText.Reading;
    private bool busy = true, parked;
    private string read = "", original = "";
    private PaletteSession? session;
    private MascotState face = MascotState.Thinking;

    private bool Live => view.Open || parked;

    /// <summary>The rectangle is drawn; the island is still in the Capture state, so the region is taken before it shows.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static async Task StartAsync(IslandWindow island, TriggerController triggers, SettingsStore store, Action<string>? log, CaptureOverlay overlay, PixelRect crop)
    {
        var engine = ScreenText.Engine();
        // Taken with no text language too: a QR code needs none.
        using var bitmap = await ScreenText.CaptureAsync(overlay.Monitor, crop, overlay.Lasso.Scale, CancellationToken.None);
        // Esc, another hotkey or the lock screen ended the capture meanwhile.
        if (triggers.Machine.State != IslandState.Capture || Program.Text is not { } text || Jobs.Shelf is not { } shelf)
        {
            return;
        }

        log?.Invoke($"screen text: px={crop} scaled to {bitmap.PixelWidth}x{bitmap.PixelHeight} language={engine?.RecognizerLanguage.LanguageTag ?? "none"}");
        triggers.PaletteFromCapture();
        await new ScreenTextRun(island, triggers, shelf, text, store, log).RunAsync(engine, bitmap);
    }

    private async Task RunAsync(OcrEngine? engine, SoftwareBitmap bitmap)
    {
        Open();
        Show(ScreenText.Reading, busy: true);
        island.Mascot.Set(face);
        try
        {
            // Both on this PC: the QR decoder on a worker thread while Windows reads the text.
            var qr = Task.Run(() => ScreenText.FindQr(bitmap));
            read = engine is null ? "" : await ScreenText.ReadAsync(engine, bitmap);
            string? code = await qr;
            if (!Live)
            {
                return;
            }

            if (code is not null)
            {
                Finish(Copied(ScreenText.QrCode(code), Copy(code)), ok: true);
                return;
            }

            if (read.Length == 0)
            {
                Finish(engine is null ? ScreenText.NoLanguage : ScreenText.NoText, ok: false);
                return;
            }

            original = read;
            bool copied = Copy(read);
            // Sent only with the setting on and a provider that can be used; otherwise nothing leaves the PC.
            if ((ScreenText.CleansUp(store.Current) ? Program.Configured(store, log) : null) is not { } configured)
            {
                Finish(Offer(Copied(ScreenText.Copied(read, noProvider: store.Current.CleanUpScreenText), copied)), ok: true);
                return;
            }

            var running = Follow(ScreenText.Session(read, configured.Provider, configured.Config, configured.Options, new SendLog(Program.SendLogPath)));
            Show(Copied(ScreenText.Cleaning(read, running.ProviderIndicator), copied), busy: true);
            await running.SendAsync();
            if (!Live)
            {
                return;
            }

            string cleaned = running.Status == PaletteStatus.Done ? ScreenText.Cleaned(running.AnswerText) : "";
            if (cleaned.Length == 0)
            {
                Finish(Offer(Copied(ScreenText.NotCleaned(read, running.ProviderIndicator, running.Error), copied)), ok: false);
                return;
            }

            Finish(Offer(Copied(ScreenText.CleanedUp(cleaned, running.ProviderIndicator), Copy(cleaned))), ok: true);
        }
        catch (Exception e)
        {
            // Nothing escapes into the dispatcher; the card says it failed.
            log?.Invoke($"screen text: failed: {e}");
            Finish(read.Length == 0 ? ScreenText.NoText with { Line = "The text could not be read. Nothing was sent." } : ScreenText.Copied(read), ok: false);
        }
    }

    // A session for the card: its provider line follows the send, sending to, then sent to.
    private PaletteSession Follow(PaletteSession running)
    {
        session = running;
        running.Changed += () =>
        {
            if (Live && running.Status == PaletteStatus.Sending && running.ProviderIndicator != last.Indicator)
            {
                Show(last with { Indicator = running.ProviderIndicator }, busy: true);
            }
        };
        return running;
    }

    private DropResult Offer(DropResult result) => ScreenText.Offer(store.Current, result);

    // The text shown goes to the provider, and only now, on the click.
    private async Task TranslateAsync()
    {
        string source = last.Body, language = ScreenText.LanguageName(store.Current.TranslateScreenTextTo);
        if (Program.Configured(store, log) is not { } configured)
        {
            Show(last with { Line = "The provider in Settings cannot be used right now. Nothing was sent." }, busy: false);
            return;
        }

        original = source;
        var running = Follow(ScreenText.Session(source, configured.Provider, configured.Config, configured.Options, new SendLog(Program.SendLogPath), language));
        face = MascotState.Thinking;
        island.Mascot.Set(face);
        Show(ScreenText.Translating(source, running.ProviderIndicator, language), busy: true);
        try
        {
            await running.SendAsync();
        }
        catch (Exception e)
        {
            log?.Invoke($"screen text: translation failed: {e}");
        }

        if (!Live)
        {
            return;
        }

        string translated = running.Status == PaletteStatus.Done ? ScreenText.Cleaned(running.AnswerText) : "";
        if (translated.Length == 0)
        {
            Finish(ScreenText.NotTranslated(source, running.ProviderIndicator, running.Error), ok: false);
            return;
        }

        Finish(Copied(ScreenText.Translated(translated, language, running.ProviderIndicator), Copy(translated)), ok: true);
    }

    private static DropResult Copied(DropResult result, bool copied) => copied ? result : result with { Line = ScreenText.ClipboardBusy };

    private void Open()
    {
        view = new DropResultView(island, triggers, shelf, text, log);
        view.Closed += Closed;
        view.Chose += Chose;
    }

    private void Closed()
    {
        // Once something went to the provider, Undo can bring the card back for a while.
        if (session is not null)
        {
            parked = true;
            float height = view.Height, scroll = view.Scroll;
            triggers.Closed(TextClosed, () => Reopen(height, scroll), Free);
        }
        else
        {
            Free();
        }
    }

    private void Free()
    {
        parked = false;
        session?.Cancel();
    }

    // Undo: the card in the pill's place, showing what the run has now, scrolled as it was.
    private bool Reopen(float height, float scroll)
    {
        if (!triggers.Reopen(height))
        {
            return false;
        }

        parked = false;
        Open();
        view.Scroll = scroll;
        Show(last, busy);
        island.Mascot.Set(face);
        return true;
    }

    // Copy puts the text shown on the clipboard again, Copy original the text before the AI changed it; either, Open
    // link, Translate in browser and Close, then close. Translate keeps the card and fills it with the translation.
    private void Chose(int i)
    {
        if (busy || i >= last.Choices.Count)
        {
            return;
        }

        string choice = last.Choices[i];
        if (choice == ScreenText.Translate)
        {
            _ = TranslateAsync();
            return;
        }

        if (choice == ScreenText.Open && !OpenLink(last.Body, "Windows could not open the link. It is copied, so you can paste it into a browser."))
        {
            return;
        }

        if (choice == ScreenText.TranslateInBrowser
            && !OpenLink(ScreenText.BrowserTranslation(last.Body, store.Current.TranslateScreenTextTo).Url, "Windows could not open the browser. Nothing was sent."))
        {
            return;
        }

        if (choice is ScreenText.Copy or ScreenText.CopyOriginal && !Copy(choice == ScreenText.Copy ? last.Body : original))
        {
            Show(last with { Line = ScreenText.ClipboardBusy }, busy: false);
            return;
        }

        triggers.Hide();
    }

    private bool OpenLink(string url, string failed)
    {
        try
        {
            DesktopEffects.OpenUrl(url);
            return true;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            log?.Invoke($"screen text: link did not open ({e.Message})");
            Show(last with { Line = failed }, busy: false);
            return false;
        }
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
            log?.Invoke($"screen text: not copied ({e.Message})");
            return false;
        }
    }

    // Drawn only while the card is up; a run kept for Undo keeps what it would show for when it comes back.
    private void Show(DropResult result, bool busy)
    {
        (last, this.busy) = (result, busy);
        if (view.Open)
        {
            view.Show(result, []);
            triggers.ZonePalette(view.Height, busy);
            // After the change to the Palette state, which seats the tomte by the prompt box: this card's row is its head.
            island.Seat = DropResultLayout.Seat;
        }
    }

    /// <summary>The last state of the card, which holds the island until the user dismisses it with Close or Esc.</summary>
    private void Finish(DropResult result, bool ok)
    {
        if (!Live)
        {
            return;
        }

        Show(result.Closable(), busy: false);
        face = ok ? MascotState.Result : MascotState.Error;
        if (view.Open)
        {
            island.Mascot.Set(face);
        }
    }
}
