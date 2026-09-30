using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Globalization;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;
using PigPdfDocument = UglyToad.PdfPig.PdfDocument;
using WinPdfDocument = Windows.Data.Pdf.PdfDocument;

namespace MdViewer.Core.Import;

public sealed class PdfImportException : Exception
{
    public PdfImportException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public sealed record PdfImportResult(
    string Markdown,
    int PageCount,
    int EmbeddedTextPageCount,
    int OcrPageCount,
    int MissingPageCount,
    IReadOnlyList<int> OcrPages,
    IReadOnlyList<int> MissingPages,
    IReadOnlyList<string> Warnings);

public enum PdfMarkdownNormalizationMode
{
    PreserveLines,
    FlowingProse
}

public sealed class PdfImportService
{
    private const string LineEnding = "\n";
    private const double OcrTargetScale = 3.0;
    private const double MinimumOcrScale = 0.05;
    private const int ReadableLetterThreshold = 10;
    private const int ReadableWordThreshold = 3;
    private const int OcrCandidateLetterThreshold = 40;
    private const int OcrCandidateWordThreshold = 8;

    private static readonly Regex HyphenatedLineBreakRegex = new(
        @"(?<prefix>\p{L}{3,})-\n(?<suffix>\p{Ll}{3,})",
        RegexOptions.Compiled);

    private static readonly Regex ExcessBlankLineRegex = new(@"\n{3,}", RegexOptions.Compiled);
    private static readonly Regex InlineWhitespaceRegex = new(@"[ \t]+", RegexOptions.Compiled);
    private static readonly Regex WordRegex = new(@"\p{L}{2,}", RegexOptions.Compiled);
    private static readonly Regex RepeatedWhitespaceRegex = new(@"[ \t]{3,}", RegexOptions.Compiled);
    private static readonly Regex LeadingIndentRegex = new(@"^\s{2,}", RegexOptions.Compiled);
    private static readonly Regex MarkdownTitleEscapeRegex = new(@"[#*_`\[\]<>\\]", RegexOptions.Compiled);

    private static readonly Regex BulletRegex = new(
        @"^\s*(?<bullet>[*+\-\u2022\u2023\u25E6\u25AA\u2013\u2014]|\d+[.)])\s+(?<text>.+)$",
        RegexOptions.Compiled);

    private static readonly Regex NumberedBulletRegex = new(@"^\d+[.)]$", RegexOptions.Compiled);

    public async Task<PdfImportResult> ImportAsMarkdownAsync(
        string pdfPath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default,
        string? ocrLanguageTag = null,
        PdfMarkdownNormalizationMode normalizationMode = PdfMarkdownNormalizationMode.PreserveLines)
    {
        if (string.IsNullOrWhiteSpace(pdfPath))
        {
            throw new ArgumentException("A PDF path is required.", nameof(pdfPath));
        }

        if (!File.Exists(pdfPath))
        {
            throw new FileNotFoundException($"Input PDF not found at {pdfPath}", pdfPath);
        }

        try
        {
            return await ImportAsMarkdownCoreAsync(
                pdfPath,
                progress,
                cancellationToken,
                ocrLanguageTag,
                normalizationMode).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PdfImportException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new PdfImportException(
                $"PDF import failed for '{Path.GetFileName(pdfPath)}'. {ex.Message}",
                ex);
        }
    }

    private static async Task<PdfImportResult> ImportAsMarkdownCoreAsync(
        string pdfPath,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        string? ocrLanguageTag,
        PdfMarkdownNormalizationMode normalizationMode)
    {
        progress?.Report("Reading PDF text...");

        PdfPageText[] pages = await Task
            .Run(() => ExtractEmbeddedText(pdfPath, cancellationToken), cancellationToken)
            .ConfigureAwait(false);

        if (pages.Length == 0)
        {
            throw new PdfImportException("The PDF does not contain any pages.");
        }

        var warnings = new List<string>();
        IReadOnlyList<int> ocrPages = Array.Empty<int>();

        if (pages.Any(ShouldRunOcr))
        {
            progress?.Report("Running Windows OCR for pages with little or no extractable text...");
            ocrPages = await FillWeakPagesWithWindowsOcrAsync(
                pdfPath,
                pages,
                warnings,
                progress,
                cancellationToken,
                ocrLanguageTag).ConfigureAwait(false);
        }

        int[] missingPages = pages
            .Where(static page => !HasReadableText(page.Text))
            .Select(static page => page.Number)
            .ToArray();

        if (missingPages.Length == pages.Length)
        {
            throw new PdfImportException("No readable text could be extracted from this PDF.");
        }

        if (missingPages.Length > 0)
        {
            warnings.Add($"No readable text was extracted from page(s): {string.Join(", ", missingPages)}.");
        }

        int embeddedTextPageCount = pages.Count(static page => !page.IsOcr && HasReadableText(page.Text));
        string markdown = await Task
            .Run(
                () => BuildMarkdown(
                    pdfPath,
                    pages,
                    embeddedTextPageCount,
                    ocrPages,
                    missingPages,
                    warnings,
                    normalizationMode),
                cancellationToken)
            .ConfigureAwait(false);

        return new PdfImportResult(
            markdown,
            pages.Length,
            embeddedTextPageCount,
            ocrPages.Count,
            missingPages.Length,
            ocrPages,
            missingPages,
            warnings);
    }

    private static PdfPageText[] ExtractEmbeddedText(
        string pdfPath,
        CancellationToken cancellationToken)
    {
        using PigPdfDocument document = PigPdfDocument.Open(pdfPath);

        var pages = Enumerable
            .Range(1, document.NumberOfPages)
            .Select(static pageNumber => new PdfPageText(pageNumber, string.Empty, IsOcr: false))
            .ToArray();

        foreach (Page page in document.GetPages())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (page.Number < 1 || page.Number > pages.Length)
            {
                continue;
            }

            string text = ContentOrderTextExtractor.GetText(page) ?? string.Empty;
            pages[page.Number - 1] = new PdfPageText(page.Number, text, IsOcr: false);
        }

        return pages;
    }

    private static async Task<IReadOnlyList<int>> FillWeakPagesWithWindowsOcrAsync(
        string pdfPath,
        PdfPageText[] pages,
        List<string> warnings,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        string? ocrLanguageTag)
    {
        if (!OperatingSystem.IsWindows())
        {
            warnings.Add("Windows OCR is unavailable on this platform.");
            progress?.Report("Windows OCR is unavailable on this platform.");
            return Array.Empty<int>();
        }

        OcrEngine? ocrEngine = CreateOcrEngine(ocrLanguageTag, warnings);

        if (ocrEngine is null)
        {
            warnings.Add("Windows OCR is unavailable for the current user languages.");
            progress?.Report("Windows OCR is unavailable for the current user languages.");
            return Array.Empty<int>();
        }

        StorageFile file = await StorageFile
            .GetFileFromPathAsync(pdfPath)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);

        WinPdfDocument pdfDocument = await WinPdfDocument
            .LoadFromFileAsync(file)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);

        if (pdfDocument.PageCount != (uint)pages.Length)
        {
            warnings.Add(
                $"PDF page count mismatch. Text extraction saw {pages.Length:N0}; Windows rendering saw {pdfDocument.PageCount:N0}. OCR may be incomplete.");
        }

        uint pageCount = Math.Min(pdfDocument.PageCount, (uint)pages.Length);
        var ocrPages = new List<int>();

        for (uint pageIndex = 0; pageIndex < pageCount; pageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int arrayIndex = checked((int)pageIndex);
            PdfPageText pageText = pages[arrayIndex];

            if (!ShouldRunOcr(pageText))
            {
                continue;
            }

            int pageNumber = arrayIndex + 1;
            progress?.Report($"Running OCR on page {pageNumber:N0} of {pageCount:N0}...");

            try
            {
                using PdfPage page = pdfDocument.GetPage(pageIndex);
                string ocrText = await RecognizePdfPageAsync(page, ocrEngine, cancellationToken).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(ocrText))
                {
                    continue;
                }

                if (ShouldUseOcrText(pageText.Text, ocrText))
                {
                    pages[arrayIndex] = pageText with { Text = ocrText, IsOcr = true };
                    ocrPages.Add(pageNumber);
                }
                else if (HasReadableText(ocrText))
                {
                    warnings.Add($"OCR on page {pageNumber:N0} was not used because embedded text looked better.");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                string warning = $"OCR failed on page {pageNumber:N0}: {ex.Message}";
                warnings.Add(warning);
                progress?.Report(warning);
            }
        }

        return ocrPages;
    }

    private static OcrEngine? CreateOcrEngine(string? ocrLanguageTag, List<string> warnings)
    {
        if (!string.IsNullOrWhiteSpace(ocrLanguageTag))
        {
            try
            {
                var language = new Language(ocrLanguageTag);

                if (OcrEngine.IsLanguageSupported(language))
                {
                    return OcrEngine.TryCreateFromLanguage(language);
                }

                warnings.Add($"Windows OCR language '{ocrLanguageTag}' is not installed or supported. Falling back to user profile languages.");
            }
            catch (Exception ex)
            {
                warnings.Add($"Windows OCR language '{ocrLanguageTag}' could not be used: {ex.Message}. Falling back to user profile languages.");
            }
        }

        return OcrEngine.TryCreateFromUserProfileLanguages();
    }

    private static async Task<string> RecognizePdfPageAsync(
        PdfPage page,
        OcrEngine ocrEngine,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var stream = new InMemoryRandomAccessStream();
        var renderOptions = CreateOcrRenderOptions(page);

        await page
            .RenderToStreamAsync(stream, renderOptions)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        stream.Seek(0);

        BitmapDecoder decoder = await BitmapDecoder
            .CreateAsync(stream)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);

        if (decoder is null)
        {
            return string.Empty;
        }

        using SoftwareBitmap bitmap = await decoder
            .GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);

        OcrResult? result = await ocrEngine
            .RecognizeAsync(bitmap)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);

        if (result is null)
        {
            return string.Empty;
        }

        return string.Join(
            "\n",
            result.Lines.Select(static line => line.Text).Where(static line => !string.IsNullOrWhiteSpace(line)));
    }

    private static PdfPageRenderOptions CreateOcrRenderOptions(PdfPage page)
    {
        double width = page.Size.Width;
        double height = page.Size.Height;

        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("PDF page has invalid dimensions.");
        }

        double scale = OcrTargetScale;
        double maxDimension = OcrEngine.MaxImageDimension;

        if (maxDimension > 0)
        {
            double maxAllowedScale = Math.Min(maxDimension / width, maxDimension / height);
            scale = Math.Min(scale, maxAllowedScale);
        }
        else
        {
            scale = Math.Max(MinimumOcrScale, scale);
        }

        scale = Math.Max(double.Epsilon, scale);

        uint destinationWidth = Math.Max(1u, (uint)Math.Round(width * scale));
        uint destinationHeight = Math.Max(1u, (uint)Math.Round(height * scale));

        if (maxDimension > 0)
        {
            uint maxPixels = Math.Max(1u, (uint)Math.Floor(maxDimension));
            destinationWidth = Math.Min(destinationWidth, maxPixels);
            destinationHeight = Math.Min(destinationHeight, maxPixels);
        }

        return new PdfPageRenderOptions
        {
            DestinationWidth = destinationWidth,
            DestinationHeight = destinationHeight,
            BitmapEncoderId = BitmapEncoder.PngEncoderId
        };
    }

    private static string BuildMarkdown(
        string pdfPath,
        PdfPageText[] pages,
        int embeddedTextPageCount,
        IReadOnlyList<int> ocrPages,
        IReadOnlyList<int> missingPages,
        IReadOnlyList<string> warnings,
        PdfMarkdownNormalizationMode normalizationMode)
    {
        var markdown = new StringBuilder();
        string title = SanitizeMarkdownTitle(Path.GetFileNameWithoutExtension(pdfPath));

        AppendDiagnosticsComment(markdown, pages.Length, embeddedTextPageCount, ocrPages, missingPages, warnings);

        markdown.Append("# ");
        markdown.Append(title);
        markdown.Append(LineEnding);
        markdown.Append(LineEnding);

        foreach (PdfPageText page in pages)
        {
            if (!HasReadableText(page.Text))
            {
                continue;
            }

            string pageMarkdown = NormalizePageText(page.Text, normalizationMode);

            if (string.IsNullOrWhiteSpace(pageMarkdown))
            {
                continue;
            }

            markdown.Append("<!-- Page ");
            markdown.Append(page.Number);
            markdown.Append(" -->");
            markdown.Append(LineEnding);
            markdown.Append(LineEnding);
            markdown.Append(pageMarkdown);
            markdown.Append(LineEnding);
            markdown.Append(LineEnding);
        }

        return NormalizeLineEndings(markdown.ToString()).TrimEnd() + LineEnding;
    }

    private static string NormalizePageText(string text, PdfMarkdownNormalizationMode normalizationMode)
    {
        return normalizationMode == PdfMarkdownNormalizationMode.PreserveLines
            ? NormalizePageTextPreserveLines(text)
            : NormalizePageTextFlowingProse(text);
    }

    private static string NormalizePageTextPreserveLines(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string normalized = NormalizeLineEndings(text);
        normalized = HyphenatedLineBreakRegex.Replace(normalized, "${prefix}${suffix}");

        var lines = normalized
            .Split('\n')
            .Select(NormalizePreservedLine);

        return ExcessBlankLineRegex.Replace(string.Join(LineEnding, lines).Trim(), "\n\n");
    }

    private static string NormalizePageTextFlowingProse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string normalized = NormalizeLineEndings(text);
        normalized = HyphenatedLineBreakRegex.Replace(normalized, "${prefix}${suffix}");

        string[] lines = normalized.Split('\n');
        var markdown = new StringBuilder();
        var paragraph = new StringBuilder();

        foreach (string rawLine in lines)
        {
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                FlushParagraph(markdown, paragraph);
                continue;
            }

            Match bulletMatch = BulletRegex.Match(rawLine);

            if (bulletMatch.Success)
            {
                FlushParagraph(markdown, paragraph);
                AppendBulletLine(markdown, rawLine, bulletMatch);
                continue;
            }

            string trimmedLine = rawLine.Trim();
            string compactLine = InlineWhitespaceRegex.Replace(trimmedLine, " ");

            if (ShouldPreserveLineBreak(rawLine))
            {
                FlushParagraph(markdown, paragraph);
                markdown.Append(NormalizePreservedLine(rawLine));
                markdown.Append(LineEnding);
                continue;
            }

            if (paragraph.Length > 0)
            {
                paragraph.Append(' ');
            }

            paragraph.Append(compactLine);
        }

        FlushParagraph(markdown, paragraph);

        return ExcessBlankLineRegex.Replace(markdown.ToString().Trim(), "\n\n");
    }

    private static bool HasReadableText(string? text)
    {
        return TextQualityScore(text) >= ReadableLetterThreshold ||
            CountWords(text) >= ReadableWordThreshold;
    }

    private static bool ShouldRunOcr(PdfPageText page)
    {
        return LooksLikeBadTextLayer(page.Text);
    }

    private static bool LooksLikeBadTextLayer(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        int chars = text.Length;
        int letters = text.Count(char.IsLetter);
        int words = WordRegex.Matches(text).Count;
        int replacementChars = text.Count(static character => character == '\uFFFD');
        int controlChars = text.Count(static character =>
            char.IsControl(character) &&
            character is not '\r' and not '\n' and not '\t');

        double letterRatio = chars == 0 ? 0 : (double)letters / chars;

        return
            words < OcrCandidateWordThreshold ||
            letters < OcrCandidateLetterThreshold ||
            replacementChars > 0 ||
            controlChars > 0 ||
            letterRatio < 0.25;
    }

    private static bool ShouldUseOcrText(string embeddedText, string ocrText)
    {
        if (!HasReadableText(ocrText))
        {
            return false;
        }

        if (!HasReadableText(embeddedText))
        {
            return true;
        }

        int embeddedScore = TextQualityScore(embeddedText);
        int ocrScore = TextQualityScore(ocrText);

        return ocrScore > embeddedScore * 2 || ocrScore >= embeddedScore + 120;
    }

    private static int TextQualityScore(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        int letters = text.Count(char.IsLetter);
        int words = WordRegex.Matches(text).Count;
        int replacementChars = text.Count(static character => character == '\uFFFD');
        int controlChars = text.Count(static character =>
            char.IsControl(character) &&
            character is not '\r' and not '\n' and not '\t');

        return letters + words * 5 - replacementChars * 20 - controlChars * 10;
    }

    private static int CountWords(string? text)
    {
        return string.IsNullOrWhiteSpace(text) ? 0 : WordRegex.Matches(text).Count;
    }

    private static bool ShouldPreserveLineBreak(string rawLine)
    {
        if (rawLine.Contains('|', StringComparison.Ordinal))
        {
            return true;
        }

        if (LeadingIndentRegex.IsMatch(rawLine))
        {
            return true;
        }

        return RepeatedWhitespaceRegex.IsMatch(rawLine);
    }

    private static string NormalizePreservedLine(string rawLine)
    {
        return rawLine.Replace('\t', ' ').TrimEnd();
    }

    private static void AppendBulletLine(StringBuilder markdown, string rawLine, Match bulletMatch)
    {
        string bullet = bulletMatch.Groups["bullet"].Value;
        string itemText = InlineWhitespaceRegex.Replace(bulletMatch.Groups["text"].Value.Trim(), " ");
        string indent = GetMarkdownListIndent(rawLine);

        if (NumberedBulletRegex.IsMatch(bullet))
        {
            string number = bullet.TrimEnd('.', ')');

            markdown.Append(indent);
            markdown.Append(number);
            markdown.Append(". ");
            markdown.Append(itemText);
            markdown.Append(LineEnding);
            return;
        }

        markdown.Append(indent);
        markdown.Append("- ");
        markdown.Append(itemText);
        markdown.Append(LineEnding);
    }

    private static string GetMarkdownListIndent(string rawLine)
    {
        int leadingWhitespace = rawLine.TakeWhile(char.IsWhiteSpace).Count();
        int normalizedIndent = Math.Min(12, leadingWhitespace / 2 * 2);
        return normalizedIndent == 0 ? string.Empty : new string(' ', normalizedIndent);
    }

    private static void FlushParagraph(StringBuilder markdown, StringBuilder paragraph)
    {
        if (paragraph.Length == 0)
        {
            return;
        }

        markdown.Append(paragraph);
        markdown.Append(LineEnding);
        markdown.Append(LineEnding);
        paragraph.Clear();
    }

    private static void AppendDiagnosticsComment(
        StringBuilder markdown,
        int pageCount,
        int embeddedTextPageCount,
        IReadOnlyList<int> ocrPages,
        IReadOnlyList<int> missingPages,
        IReadOnlyList<string> warnings)
    {
        markdown.Append("<!--");
        markdown.Append(LineEnding);
        markdown.Append("PDF import: ");
        markdown.Append(pageCount);
        markdown.Append(" page(s)");
        markdown.Append(LineEnding);
        markdown.Append("Embedded text pages: ");
        markdown.Append(embeddedTextPageCount);
        markdown.Append(LineEnding);
        markdown.Append("OCR pages: ");
        markdown.Append(ocrPages.Count == 0 ? "none" : string.Join(", ", ocrPages));
        markdown.Append(LineEnding);
        markdown.Append("Missing pages: ");
        markdown.Append(missingPages.Count == 0 ? "none" : string.Join(", ", missingPages));
        markdown.Append(LineEnding);

        foreach (string warning in warnings)
        {
            markdown.Append("Warning: ");
            markdown.Append(warning);
            markdown.Append(LineEnding);
        }

        markdown.Append("-->");
        markdown.Append(LineEnding);
        markdown.Append(LineEnding);
    }

    private static string SanitizeMarkdownTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "Untitled";
        }

        string sanitized = MarkdownTitleEscapeRegex.Replace(title, string.Empty).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "Untitled" : sanitized;
    }

    private static string NormalizeLineEndings(string text)
    {
        return text.Replace("\r\n", "\n").Replace('\r', '\n');
    }

    private sealed record PdfPageText(int Number, string Text, bool IsOcr);
}
