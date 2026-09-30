using System.Text;

namespace MdViewer.Core.Documents;

public sealed record TextFile(string Text, Encoding Encoding, string EncodingLabel);

/// <summary>Reads and writes text documents without losing their encoding.</summary>
public static class DocumentFile
{
    public const long MaxBytes = 64L * 1024 * 1024;
    public static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly Encoding Utf8Bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);

    static DocumentFile() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static readonly string[] MarkdownExtensions = [".md", ".markdown", ".mdown", ".mkd", ".txt"];
    public static readonly string[] PandocImportExtensions = [".docx", ".html", ".htm", ".epub", ".odt", ".rtf"];
    public static bool IsMarkdown(string path) => MarkdownExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static async Task<TextFile> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException($"{path} does not exist.", path);
        if (info.Length > MaxBytes) throw new IOException($"{info.Name} is larger than the 64 MB document limit.");
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        return Decode(bytes);
    }

    public static TextFile Decode(byte[] bytes)
    {
        ReadOnlySpan<byte> span = bytes;
        if (span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) return new(Utf8.GetString(span[3..]), Utf8Bom, "UTF-8 BOM");
        if (span.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE])) return new(Encoding.Unicode.GetString(span[2..]), Encoding.Unicode, "UTF-16 LE");
        if (span.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF])) return new(Encoding.BigEndianUnicode.GetString(span[2..]), Encoding.BigEndianUnicode, "UTF-16 BE");
        try { return new(StrictUtf8.GetString(span), Utf8, "UTF-8"); }
        catch (DecoderFallbackException)
        {
            // Not valid UTF-8: most likely a legacy ANSI file. Keep that encoding for saving.
            var ansi = Encoding.GetEncoding(1252);
            return new(ansi.GetString(span), ansi, "Windows-1252");
        }
    }

    /// <summary>Writes through a temporary file so a failed save never truncates the original.</summary>
    public static async Task WriteAsync(string path, string text, Encoding encoding, CancellationToken cancellationToken = default)
    {
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full) ?? throw new IOException($"{path} has no parent folder.");
        var temporary = Path.Combine(directory, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var preamble = encoding.GetPreamble();
                await stream.WriteAsync(preamble, cancellationToken);
                await stream.WriteAsync(encoding.GetBytes(text), cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporary, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>Characters that cannot be represented in a legacy encoding force a UTF-8 save.</summary>
    public static (Encoding Encoding, string Label) EncodingFor(string text, Encoding preferred, string label)
    {
        if (preferred.CodePage != 1252) return (preferred, label);
        var strict = Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        try { strict.GetBytes(text); return (preferred, label); }
        catch (EncoderFallbackException) { return (Utf8, "UTF-8"); }
    }
}
