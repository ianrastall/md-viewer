using System.Text.Json;

namespace MdViewer.Interop;

public enum FileKind { Markdown, Pandoc, Pdf, Export }

public sealed record FileType(FileKind Kind, string Extension, string Label);

/// <summary>A document opened by the C++ core: Markdown as is, or another format converted to Markdown.</summary>
public sealed record OpenedDocument(bool IsMarkdown, string Encoding, string Summary, string Text);

public sealed class ToolStatus
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Purpose { get; init; } = "";
    public bool Installed { get; init; }
    public string? Path { get; init; }
    public string? Source { get; init; }
    public string? Version { get; init; }
    public string? Latest { get; init; }
    public bool UpdateAvailable { get; init; }
    public string? LatestError { get; init; }
    public string Action { get; init; } = "none";
    public string? ActionNote { get; init; }
    public string? Note { get; init; }
    public string[] Languages { get; init; } = [];
}

public sealed class OcrStatus
{
    public string Engine { get; init; } = "auto";
    public string Languages { get; init; } = "eng";
    public string Active { get; init; } = "windows";
}

public sealed class ToolsStatus
{
    public ToolStatus[] Tools { get; init; } = [];
    public OcrStatus Ocr { get; init; } = new();
    public bool CheckedLatest { get; init; }
    public ToolStatus? this[string id] => Tools.FirstOrDefault(t => t.Id == id);
}

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

    /// <summary>OCR for scanned PDF pages: "auto", "windows", or "tesseract", and Tesseract languages such as "eng+deu".</summary>
    public static void ConfigureOcr(string engine, string languages) => Native.Call(() => { Native.ConfigureOcr(engine, languages); return 0; });

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>Installed tools and, with <paramref name="checkLatest"/>, their newest official releases.</summary>
    public static Task<ToolsStatus> ToolsStatusAsync(bool checkLatest, IProgress<string>? progress, CancellationToken cancellation = default) => Native.RunAsync(() => Native.Call(() =>
    {
        var json = Native.WithProgress(progress, cancellation, (callback, context) => Native.Text(Native.ToolsStatus(checkLatest ? 1 : 0, Native.Callback(callback), context)));
        return JsonSerializer.Deserialize<ToolsStatus>(json, JsonOptions) ?? throw new NativeCoreException("The C++ core returned no tool information.");
    }));

    /// <summary>Installs or updates "pandoc" or "tesseract"; returns a status line.</summary>
    public static Task<string> InstallToolAsync(string tool, IProgress<string>? progress, CancellationToken cancellation = default) => Native.RunAsync(() => Native.Call(() =>
        Native.WithProgress(progress, cancellation, (callback, context) => Native.Text(Native.ToolInstall(tool, Native.Callback(callback), context)))));

    public static Task<string> CrawlAsync(string startUrl, int maxPages, IProgress<string>? progress, CancellationToken cancellation) => Native.RunAsync(() => Native.Call(() =>
        Native.WithProgress(progress, cancellation, (callback, context) => Native.Text(Native.Crawl(startUrl, maxPages, Native.Callback(callback), context)))));
}
