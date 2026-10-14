using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Exceptions;

namespace Tomte.AI.Files;

/// <summary>
/// A PDF's text layer through PdfPig, a managed parser: a PDF is untrusted input, and a bug in managed code is an
/// exception, not memory corruption. The file is read through its stream, one page at a time, and reading stops at the
/// character cap. A PDF without text (a scan) gives empty text and its page count; Tomte does not run OCR. Kept in its
/// own class so PdfPig's assemblies load only when a PDF is read.
/// </summary>
public static class PdfExtraction
{
    public static Extracted Read(Stream file, FileLimits limits, CancellationToken ct)
    {
        if (file.Length > limits.MaxBytes)
            throw new FileReadException($"is larger than the {limits.MaxBytes >> 20} MB Tomte opens.");
        PdfDocument document;
        try
        {
            document = PdfDocument.Open(file, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
        }
        catch (PdfDocumentEncryptedException)
        {
            throw new FileReadException("is protected by a password, so it could not be read.");
        }
        catch (Exception e) when (e is not (OperationCanceledException or OutOfMemoryException or FileReadException))
        {
            throw new FileReadException("could not be read: it is damaged or not a PDF.");
        }

        using (document)
        {
            int pages, failed = 0, read = 0;
            try
            {
                pages = document.NumberOfPages;
            }
            catch (Exception e) when (e is not (OperationCanceledException or OutOfMemoryException))
            {
                throw new FileReadException("could not be read: it is damaged or not a PDF.");
            }

            if (pages == 0)
                throw new FileReadException("could not be read: it has no pages.");

            var text = new StringBuilder();
            for (int number = 1; number <= pages && text.Length < limits.MaxChars; number++)
            {
                ct.ThrowIfCancellationRequested();
                read++;
                try
                {
                    var page = ContentOrderTextExtractor.GetText(document.GetPage(number));
                    if (page.Length > 0)
                        text.Append(text.Length == 0 ? "" : "\n\n").Append(page);
                }
                // One broken page leaves the others readable.
                catch (Exception e) when (e is not (OperationCanceledException or OutOfMemoryException))
                {
                    failed++;
                }
            }

            if (failed == read)
                throw new FileReadException("could not be read: it is damaged or not a PDF.");
            bool cut = text.Length >= limits.MaxChars || read < pages;
            string result = text.Length > limits.MaxChars ? text.ToString(0, limits.MaxChars) : text.ToString();
            return new Extracted(string.IsNullOrWhiteSpace(result) ? "" : result, cut && result.Length > 0, pages, read);
        }
    }
}
