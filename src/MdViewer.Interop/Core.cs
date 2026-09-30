namespace MdViewer.Interop;

public enum FileKind { Markdown, Pandoc, Pdf, Export }

public sealed record FileType(FileKind Kind, string Extension, string Label);

/// <summary>A document opened by the C++ core: Markdown as is, or another format converted to Markdown.</summary>
public sealed record OpenedDocument(bool IsMarkdown, string Encoding, string Summary, string Text);

public sealed record FetchedPandoc(string Version, string Path);

/// <summary>Thin async wrappers over the C++ core. Every operation runs natively; C# only marshals.</summary>
public static unsafe class Core
{
    private static readonly Lazy<IReadOnlyList<FileType>> fileTypes = new(() => Native.Call(() =>
        Native.Text(Native.FileTypes()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split('\t'))
            .Select(parts => new FileType(Enum.Parse<FileKind>(parts[0], ignoreCase: true), parts[1], parts[2])).ToArray()));

    /// <summary>Where the core keeps a fetched Pandoc and its filter (the package's LocalState when installed).</summary>
    public static void Configure(string dataDirectory) => Native.Call(() => { Native.Configure(dataDirectory); return 0; });

    public static IReadOnlyList<FileType> FileTypes => fileTypes.Value;

    public static FileKind? KindOf(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Length == 0) return FileKind.Markdown;
        return FileTypes.FirstOrDefault(t => t.Kind != FileKind.Export && t.Extension.Equals(extension, StringComparison.OrdinalIgnoreCase))?.Kind;
    }

    public static Task<OpenedDocument> OpenAsync(string path, IProgress<string>? progress, CancellationToken cancellation) => Native.RunAsync(() => Native.Call(() =>
    {
        var payload = Native.WithProgress(progress, cancellation, (callback, context) => Native.Text(Native.Open(path, Native.Callback(callback), context)));
        var header = payload.Split('\n', 4);
        return new OpenedDocument(header[0] == "markdown", header[1], header[2], header[3]);
    }));

    /// <summary>Saves in the given encoding; returns the encoding actually used.</summary>
    public static Task<string> SaveAsync(string path, string text, string encoding) => Native.RunAsync(() => Native.Call(() =>
        Native.WithText(text, (utf8, length) => Native.Text(Native.Save(path, (byte*)utf8, length, encoding)))));

    public static string PandocPath() => Native.Call(() => Native.Text(Native.PandocPath()));

    public static Task<string> FormatAsync(string markdown, CancellationToken cancellation = default) => Native.RunAsync(() => Native.Call(() =>
        Native.WithText(markdown, (utf8, length) => Native.WithProgress(null, cancellation, (callback, context) =>
            Native.Text(Native.PandocFormat((byte*)utf8, length, Native.Callback(callback), context))))));

    public static Task ExportAsync(string markdown, string target, string? resourceDirectory, CancellationToken cancellation = default) => Native.RunAsync(() => Native.Call(() =>
        Native.WithText(markdown, (utf8, length) => Native.WithProgress(null, cancellation, (callback, context) =>
            Native.Text(Native.PandocExport((byte*)utf8, length, target, resourceDirectory, Native.Callback(callback), context))))));

    public static Task<FetchedPandoc> FetchPandocAsync(IProgress<string>? progress, CancellationToken cancellation = default) => Native.RunAsync(() => Native.Call(() =>
    {
        var lines = Native.WithProgress(progress, cancellation, (callback, context) => Native.Text(Native.PandocFetch(Native.Callback(callback), context))).Split('\n', 2);
        return new FetchedPandoc(lines[0], lines[1]);
    }));

    public static Task<string> CrawlAsync(string startUrl, int maxPages, IProgress<string>? progress, CancellationToken cancellation) => Native.RunAsync(() => Native.Call(() =>
        Native.WithProgress(progress, cancellation, (callback, context) => Native.Text(Native.Crawl(startUrl, maxPages, Native.Callback(callback), context)))));
}
