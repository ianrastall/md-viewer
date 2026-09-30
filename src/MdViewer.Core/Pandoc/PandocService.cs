using System.Diagnostics;
using System.Text;
using MdViewer.Core.Documents;

namespace MdViewer.Core.Pandoc;

public sealed class PandocException(string message) : InvalidOperationException(message);

/// <summary>Import, export, and formatting through an external pandoc.exe.</summary>
public sealed class PandocService
{
    /// <summary>
    /// Pandoc's Markdown, minus the extensions md-viewer's renderer cannot display
    /// (attribute blocks, fenced divs, bracketed spans, and non-pipe tables), with ATX headings and no hard wrapping.
    /// </summary>
    public const string MarkdownWriter = "markdown-header_attributes-link_attributes-fenced_code_attributes-fenced_divs-native_divs-bracketed_spans-native_spans-grid_tables-multiline_tables-simple_tables";
    // With raw HTML enabled, Pandoc would still write <div>/<span> wrappers; unwrap them and keep their content.
    private const string UnwrapFilter = "function Div(el) return el.content end\nfunction Span(el) return el.content end\n";
    private static readonly Lazy<string> UnwrapFilterPath = new(() =>
    {
        Directory.CreateDirectory(ViewerSettings.DataDirectory);
        var path = Path.Combine(ViewerSettings.DataDirectory, "unwrap-v1.lua");
        if (!File.Exists(path) || File.ReadAllText(path) != UnwrapFilter) File.WriteAllText(path, UnwrapFilter);
        return path;
    });
    private static string[] WriterOptions => ["--wrap=none", "--markdown-headings=atx", "--lua-filter=" + UnwrapFilterPath.Value];

    public static readonly IReadOnlyList<(string Label, string Extension, string Format)> ExportFormats =
    [
        ("Word document", ".docx", "docx"),
        ("HTML document", ".html", "html"),
        ("EPUB publication", ".epub", "epub"),
        ("Rich Text Format", ".rtf", "rtf"),
        ("OpenDocument text", ".odt", "odt"),
        ("LaTeX document", ".tex", "latex"),
        ("Typst document", ".typ", "typst"),
        ("reStructuredText", ".rst", "rst"),
        ("Org mode document", ".org", "org")
    ];

    public static string PandocPath => PandocLocator.Find() ?? throw new PandocException(
        "Pandoc was not found. Use Fetch Pandoc (in the ⋯ menu) to download it, or install Pandoc and add it to PATH.");

    public async Task<string> ImportAsync(string inputPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(inputPath)) throw new FileNotFoundException($"{inputPath} does not exist.", inputPath);
        var extension = Path.GetExtension(inputPath).ToLowerInvariant();
        var reader = extension switch { ".htm" or ".html" => "html", ".docx" => "docx", ".epub" => "epub", ".odt" => "odt", ".rtf" => "rtf", _ => null };
        List<string> arguments = ["-s", inputPath, "-t", MarkdownWriter, .. WriterOptions];
        if (reader is not null) arguments.InsertRange(0, ["-f", reader]);
        return await RunAsync(arguments, input: null, Path.GetDirectoryName(Path.GetFullPath(inputPath)), timeout: null, cancellationToken);
    }

    public async Task<string> FormatAsync(string markdown, CancellationToken cancellationToken = default)
    {
        var (metadata, body) = SplitMetadataBlock(markdown);
        // The metadata block is kept verbatim; Pandoc would otherwise rewrite or drop it.
        var formatted = await RunAsync(["-f", "markdown-yaml_metadata_block", "-t", MarkdownWriter, .. WriterOptions], body, null, null, cancellationToken);
        if (metadata is null) return formatted;
        return string.IsNullOrWhiteSpace(formatted) ? metadata + "\n" : metadata + "\n\n" + formatted.TrimStart();
    }

    public async Task ExportAsync(string markdown, string targetPath, string? resourceDirectory, CancellationToken cancellationToken = default)
    {
        var extension = Path.GetExtension(targetPath).ToLowerInvariant();
        var format = ExportFormats.FirstOrDefault(f => f.Extension == extension).Format
            ?? throw new NotSupportedException($"Pandoc export does not support {extension} files.");
        var directory = Path.GetDirectoryName(Path.GetFullPath(targetPath));
        if (directory is null || !Directory.Exists(directory)) throw new DirectoryNotFoundException($"The folder for {targetPath} does not exist.");
        List<string> arguments = ["-s", "-f", "markdown", "-t", format, "-o", targetPath];
        if (resourceDirectory is not null) arguments.Add("--resource-path=" + resourceDirectory);
        await RunAsync(arguments, markdown, resourceDirectory, null, cancellationToken);
    }

    /// <summary>Converts HTML or MediaWiki text for the crawler; failures return an empty page.</summary>
    public async Task<string> ConvertToMarkdownAsync(string fromFormat, string input, IProgress<string> progress, CancellationToken cancellationToken)
    {
        try
        {
            return await RunAsync(["--from", fromFormat, "--to", MarkdownWriter, .. WriterOptions, "--strip-comments"], input, null, TimeSpan.FromSeconds(90), cancellationToken);
        }
        catch (PandocException ex)
        {
            progress.Report($"PANDOC failed; using empty fallback. {ex.Message}");
            return "";
        }
    }

    internal static (string? Metadata, string Body) SplitMetadataBlock(string markdown)
    {
        var normalized = markdown.Replace("\r\n", "\n").Replace('\r', '\n');
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal) || normalized.Length <= 4 || normalized[4] == '\n') return (null, markdown);
        var position = 4;
        while (position < normalized.Length)
        {
            var end = normalized.IndexOf('\n', position);
            var line = end < 0 ? normalized[position..] : normalized[position..end];
            var next = end < 0 ? normalized.Length : end + 1;
            if (line.Trim() is "---" or "...") return (normalized[..next].TrimEnd(), normalized[next..].TrimStart('\n'));
            position = next;
        }
        return (null, markdown);
    }

    private static async Task<string> RunAsync(IEnumerable<string> arguments, string? input, string? workingDirectory, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = PandocPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = input is not null,
            StandardOutputEncoding = DocumentFile.Utf8,
            StandardErrorEncoding = DocumentFile.Utf8,
            StandardInputEncoding = input is null ? null : DocumentFile.Utf8,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (workingDirectory is not null) startInfo.WorkingDirectory = workingDirectory;
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo) ?? throw new PandocException("Pandoc could not be started.");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout is { } span) limit.CancelAfter(span);
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(limit.Token);
            var error = process.StandardError.ReadToEndAsync(limit.Token);
            if (input is not null)
            {
                await process.StandardInput.WriteAsync(input.AsMemory(), limit.Token);
                process.StandardInput.Close();
            }
            await process.WaitForExitAsync(limit.Token);
            var text = await output;
            var diagnostics = (await error).Trim();
            if (process.ExitCode != 0)
                throw new PandocException($"Pandoc exited with code {process.ExitCode}. {diagnostics}".Trim());
            return text;
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (cancellationToken.IsCancellationRequested) throw;
            throw new PandocException("Pandoc timed out.");
        }
    }
}

public static class PandocLocator
{
    public static string ToolDirectory => Path.Combine(ViewerSettings.DataDirectory, "pandoc");
    public static string FetchedPath => Path.Combine(ToolDirectory, "pandoc.exe");

    public static IEnumerable<string> Candidates()
    {
        yield return FetchedPath;
        yield return Path.Combine(AppContext.BaseDirectory, "pandoc.exe");
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string candidate;
            try { candidate = Path.Combine(directory, "pandoc.exe"); }
            catch (ArgumentException) { continue; }
            yield return candidate;
        }
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pandoc", "pandoc.exe");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Pandoc", "pandoc.exe");
    }

    public static string? Find() => Candidates().FirstOrDefault(File.Exists);
}
