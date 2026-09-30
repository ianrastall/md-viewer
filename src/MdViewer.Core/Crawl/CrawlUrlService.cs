using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using MdViewer.Core.Pandoc;

namespace MdViewer.Core.Crawl;

public sealed class CrawlerOptions
{
    public required TimeSpan DefaultDelay { get; init; }
    public required TimeSpan RequestTimeout { get; init; }
    public required int MaxPages { get; init; }
    public required int MaxPageBytes { get; init; }
    public required int MaxRetries { get; init; }
    public required bool SameBasePathOnly { get; init; }
    public required bool RespectRobotsTxt { get; init; }
}

public sealed class CrawlUrlService
{
    private const string UserAgent = "md-viewer/2.0 (single-threaded; personal docs archival)";

    public async Task<string> CrawlAsync(Uri startUrl, CrawlerOptions options, IProgress<string> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(startUrl);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(progress);

        string pandocPath = PandocService.PandocPath;
        progress.Report($"Pandoc {pandocPath}");
        progress.Report($"Start {startUrl}");

        using var crawler = new DocumentationCrawler(options, new PandocService(), UserAgent, progress);
        CrawlResult result = await crawler.CrawlAsync(startUrl, cancellationToken);

        if (result.Pages.Count == 0)
        {
            throw new InvalidOperationException("No content pages were found.");
        }

        progress.Report($"Rendering {result.Pages.Count} pages");
        string collatedMarkdown = MarkdownRenderer.Render(startUrl, result, UserAgent, options);
        return collatedMarkdown.Trim();
    }
}

internal sealed record CrawlResult(
    string Generator,
    IReadOnlyList<CrawledPage> Pages,
    int VisitedUrlCount,
    int SkippedUrlCount
);

internal sealed record CrawledPage(
    Uri Url,
    string Title,
    string Markdown
);

internal sealed class DocumentationCrawler : IDisposable
{
    private readonly CrawlerOptions _options;
    private readonly PandocService _pandoc;
    private readonly HttpClient _http;
    private readonly HtmlParser _htmlParser = new();
    private readonly string _userAgent;
    private readonly IProgress<string> _progress;
    private DateTimeOffset _lastRequestUtc = DateTimeOffset.MinValue;

    public DocumentationCrawler(CrawlerOptions options, PandocService pandoc, string userAgent, IProgress<string> progress)
    {
        _options = options;
        _pandoc = pandoc;
        _userAgent = userAgent;
        _progress = progress;

        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression =
                DecompressionMethods.GZip |
                DecompressionMethods.Deflate |
                DecompressionMethods.Brotli,
            MaxConnectionsPerServer = 1,
            AllowAutoRedirect = true
        };

        _http = new HttpClient(handler)
        {
            Timeout = options.RequestTimeout
        };

        _http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        _http.DefaultRequestHeaders.Accept.ParseAdd("text/html");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/xhtml+xml");
    }

    public void Dispose()
    {
        _http.Dispose();
    }

    public async Task<CrawlResult> CrawlAsync(Uri startUrl, CancellationToken cancellationToken)
    {
        Uri normalizedStart = UrlTools.Normalize(startUrl);
        Uri baseUrl = UrlTools.GetBaseUrl(normalizedStart);

        _progress.Report($"Base {baseUrl}");

        RobotsTxt robots = _options.RespectRobotsTxt
            ? await FetchRobotsTxtAsync(normalizedStart, cancellationToken)
            : RobotsTxt.AllowAll;

        TimeSpan effectiveDelay = robots.CrawlDelay is { } crawlDelay && crawlDelay > _options.DefaultDelay
            ? crawlDelay
            : _options.DefaultDelay;

        if (robots.CrawlDelay is not null)
        {
            _progress.Report($"Robots crawl-delay {robots.CrawlDelay.Value.TotalSeconds:0.###} seconds");
        }

        _progress.Report($"Actual delay {effectiveDelay.TotalSeconds:0.###} seconds");

        if (MediaWikiPage.TryCreate(normalizedStart, out MediaWikiPage? mediaWikiPage) &&
            mediaWikiPage is not null)
        {
            CrawlResult? mediaWikiResult = await TryCrawlMediaWikiPageAsync(
                mediaWikiPage,
                robots,
                effectiveDelay,
                cancellationToken
            );

            if (mediaWikiResult is not null)
            {
                return mediaWikiResult;
            }

            _progress.Report("WIKI raw wikitext unavailable; falling back to HTML crawl.");
        }

        var queue = new Queue<Uri>();
        var queued = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pages = new List<CrawledPage>();
        int skipped = 0;
        string generator = "_default";

        queue.Enqueue(normalizedStart);
        queued.Add(normalizedStart.AbsoluteUri);

        while (queue.Count > 0 && visited.Count < _options.MaxPages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Uri url = queue.Dequeue();
            string key = url.AbsoluteUri;

            if (!visited.Add(key))
            {
                continue;
            }

            if (!robots.IsAllowed(url))
            {
                skipped++;
                _progress.Report($"ROBOTS {url}");
                continue;
            }

            _progress.Report($"GET {url}");

            FetchResult fetch = await FetchHtmlAsync(url, effectiveDelay, cancellationToken);

            if (!fetch.Success)
            {
                skipped++;
                _progress.Report($"SKIP {fetch.Message}");
                continue;
            }

            if (HtmlDiagnostics.LooksLikeDynamicShell(fetch.Html!))
            {
                _progress.Report($"DYNAMIC likely JavaScript-rendered shell: {url}");
            }

            IDocument document = await _htmlParser.ParseDocumentAsync(fetch.Html!, cancellationToken);

            if (visited.Count == 1)
            {
                generator = GeneratorDetector.Detect(document);
                _progress.Report($"GEN {generator}");
            }

            ContentExtractionResult extraction = ContentExtraction.ExtractMainHtml(document, url, generator);
            _progress.Report($"CONTENT {extraction.SourceLabel}");

            IReadOnlyList<Uri> links = LinkDiscovery.DiscoverLinks(
                document,
                extraction.Content,
                url,
                baseUrl,
                generator,
                _options.SameBasePathOnly
            );

            foreach (Uri link in links)
            {
                string linkKey = link.AbsoluteUri;

                if (!visited.Contains(linkKey) && queued.Add(linkKey))
                {
                    queue.Enqueue(link);
                }
            }

            string title = ContentExtraction.GetTitle(document, url);
            string markdown = await _pandoc.ConvertToMarkdownAsync("html", extraction.Html, _progress, cancellationToken);

            markdown = MarkdownPostProcessor.Clean(markdown);

            if (!string.IsNullOrWhiteSpace(markdown))
            {
                pages.Add(new CrawledPage(url, title, markdown));
                _progress.Report($"OK {title}");
            }
            else
            {
                skipped++;
                _progress.Report("EMPTY No Markdown content extracted.");
            }
        }

        _progress.Report($"Pages {pages.Count}; visited {visited.Count}; skipped {skipped}");
        return new CrawlResult(generator, pages, visited.Count, skipped);
    }

    private async Task<CrawlResult?> TryCrawlMediaWikiPageAsync(
        MediaWikiPage page,
        RobotsTxt robots,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        if (robots.IsAllowed(page.ApiParseUrl))
        {
            _progress.Report($"WIKI parsed article {page.Title}");
            _progress.Report($"GET {page.ApiParseUrl}");

            FetchResult parsedFetch = await FetchJsonAsync(page.ApiParseUrl, delay, cancellationToken);
            string? parseMessage = null;

            if (parsedFetch.Success &&
                TryReadMediaWikiParsedArticle(parsedFetch.Html!, out MediaWikiParsedArticle? article, out parseMessage) &&
                article is not null)
            {
                string parsedMarkdown = await ConvertParsedMediaWikiArticleAsync(
                    page,
                    article,
                    cancellationToken
                );

                if (!string.IsNullOrWhiteSpace(parsedMarkdown))
                {
                    _progress.Report($"OK {article.Title}");
                    return new CrawlResult("mediawiki-api", [new CrawledPage(page.PageUrl, article.Title, parsedMarkdown)], 1, 0);
                }

                _progress.Report("WIKI parsed HTML conversion produced no Markdown.");
            }
            else
            {
                _progress.Report($"WIKI parsed HTML failed: {parsedFetch.Message}{parseMessage}");
            }
        }
        else
        {
            _progress.Report($"WIKI parsed article blocked by robots: {page.ApiParseUrl}");
        }

        if (!robots.IsAllowed(page.RawUrl))
        {
            _progress.Report($"WIKI raw blocked by robots: {page.RawUrl}");
            return null;
        }

        _progress.Report($"WIKI raw fallback {page.Title}");
        _progress.Report($"GET {page.RawUrl}");

        FetchResult fetch = await FetchRawTextAsync(page.RawUrl, delay, cancellationToken);

        if (!fetch.Success)
        {
            _progress.Report($"WIKI raw failed: {fetch.Message}");
            return null;
        }

        string markdown = await _pandoc.ConvertToMarkdownAsync("mediawiki",
            fetch.Html!,
            _progress,
            cancellationToken
        );

        markdown = MarkdownPostProcessor.Clean(markdown, page.PageUrl);

        if (string.IsNullOrWhiteSpace(markdown))
        {
            _progress.Report("WIKI raw conversion produced no Markdown.");
            return null;
        }

        _progress.Report($"OK {page.Title}");
        return new CrawlResult("mediawiki", [new CrawledPage(page.PageUrl, page.Title, markdown)], 1, 0);
    }

    private async Task<string> ConvertParsedMediaWikiArticleAsync(
        MediaWikiPage page,
        MediaWikiParsedArticle article,
        CancellationToken cancellationToken)
    {
        IDocument document = await _htmlParser.ParseDocumentAsync(article.Html, cancellationToken);
        ContentExtractionResult extraction = ContentExtraction.ExtractMainHtml(document, page.PageUrl, "mediawiki");
        _progress.Report($"CONTENT {extraction.SourceLabel}");

        string markdown = await _pandoc.ConvertToMarkdownAsync("html",
            extraction.Html,
            _progress,
            cancellationToken
        );

        return MarkdownPostProcessor.Clean(markdown, page.PageUrl);
    }

    private static bool TryReadMediaWikiParsedArticle(
        string json,
        out MediaWikiParsedArticle? article,
        out string? message)
    {
        article = null;
        message = null;

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            if (root.TryGetProperty("error", out JsonElement error))
            {
                message = error.TryGetProperty("info", out JsonElement info)
                    ? " " + info.GetString()
                    : "";

                return false;
            }

            if (!root.TryGetProperty("parse", out JsonElement parse) ||
                !parse.TryGetProperty("text", out JsonElement textElement))
            {
                message = " response did not contain parse.text.";
                return false;
            }

            string? html = textElement.GetString();

            if (string.IsNullOrWhiteSpace(html))
            {
                message = " parse.text was empty.";
                return false;
            }

            string title = pageTitleFromParse(parse);
            article = new MediaWikiParsedArticle(title, html);
            return true;
        }
        catch (JsonException ex)
        {
            message = " invalid JSON: " + ex.Message;
            return false;
        }

        static string pageTitleFromParse(JsonElement parse)
        {
            if (parse.TryGetProperty("displaytitle", out JsonElement displayTitle) &&
                displayTitle.GetString() is { Length: > 0 } displayTitleText)
            {
                return HtmlToPlainText(displayTitleText);
            }

            if (parse.TryGetProperty("title", out JsonElement title) &&
                title.GetString() is { Length: > 0 } titleText)
            {
                return HtmlToPlainText(titleText);
            }

            return "Wikipedia article";
        }
    }

    private static string HtmlToPlainText(string html)
    {
        string text = Regex.Replace(html, @"<[^>]+>", " ");
        text = WebUtility.HtmlDecode(text);
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    private async Task<RobotsTxt> FetchRobotsTxtAsync(Uri startUrl, CancellationToken cancellationToken)
    {
        Uri robotsUrl = new($"{startUrl.Scheme}://{startUrl.Authority}/robots.txt");

        try
        {
            await RespectDelayAsync(_options.DefaultDelay, cancellationToken);

            using var request = new HttpRequestMessage(HttpMethod.Get, robotsUrl);
            using HttpResponseMessage response = await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken
            );

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _progress.Report("Robots none found; proceeding cautiously.");
                return RobotsTxt.AllowAll;
            }

            if ((int)response.StatusCode >= 500)
            {
                _progress.Report($"Robots server returned {(int)response.StatusCode}; treating as disallow-all.");
                return RobotsTxt.DisallowAll;
            }

            if (!response.IsSuccessStatusCode)
            {
                _progress.Report($"Robots HTTP {(int)response.StatusCode}; proceeding cautiously.");
                return RobotsTxt.AllowAll;
            }

            string text = await response.Content.ReadAsStringAsync(cancellationToken);
            return RobotsTxt.Parse(text, _userAgent);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException or IOException)
        {
            _progress.Report($"Robots could not read robots.txt ({ex.Message}); proceeding cautiously.");
            return RobotsTxt.AllowAll;
        }
    }

    private async Task<FetchResult> FetchHtmlAsync(Uri url, TimeSpan delay, CancellationToken cancellationToken)
    {
        return await FetchStringAsync(
            url,
            delay,
            requireHtml: true,
            ["text/html", "application/xhtml+xml"],
            cancellationToken
        );
    }

    private async Task<FetchResult> FetchRawTextAsync(Uri url, TimeSpan delay, CancellationToken cancellationToken)
    {
        return await FetchStringAsync(
            url,
            delay,
            requireHtml: false,
            ["text/plain", "text/x-wiki", "*/*;q=0.5"],
            cancellationToken
        );
    }

    private async Task<FetchResult> FetchJsonAsync(Uri url, TimeSpan delay, CancellationToken cancellationToken)
    {
        return await FetchStringAsync(
            url,
            delay,
            requireHtml: false,
            ["application/json", "text/json", "*/*;q=0.5"],
            cancellationToken
        );
    }

    private async Task<FetchResult> FetchStringAsync(
        Uri url,
        TimeSpan delay,
        bool requireHtml,
        IReadOnlyList<string> acceptHeaders,
        CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= _options.MaxRetries; attempt++)
        {
            await RespectDelayAsync(delay, cancellationToken);

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);

                request.Headers.Accept.Clear();

                foreach (string acceptHeader in acceptHeaders)
                {
                    request.Headers.Accept.ParseAdd(acceptHeader);
                }

                using HttpResponseMessage response = await _http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken
                );

                if (ShouldRetry(response.StatusCode) && attempt < _options.MaxRetries)
                {
                    TimeSpan retryDelay = GetRetryDelay(response, attempt);
                    _progress.Report($"WAIT HTTP {(int)response.StatusCode}; retrying after {retryDelay.TotalSeconds:0.###} seconds");
                    await Task.Delay(retryDelay, cancellationToken);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    return FetchResult.Fail($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
                }

                string? mediaType = response.Content.Headers.ContentType?.MediaType;

                if (requireHtml && !LooksLikeHtml(url, mediaType))
                {
                    return FetchResult.Fail($"non-HTML content-type: {mediaType ?? "(none)"}");
                }

                string html = await ReadLimitedStringAsync(response.Content, _options.MaxPageBytes, cancellationToken);

                return FetchResult.Ok(html);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException or IOException)
            {
                if (attempt < _options.MaxRetries)
                {
                    TimeSpan retryDelay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                    _progress.Report($"WAIT {ex.Message}; retrying after {retryDelay.TotalSeconds:0.###} seconds");
                    await Task.Delay(retryDelay, cancellationToken);
                    continue;
                }

                return FetchResult.Fail(ex.Message);
            }
        }

        return FetchResult.Fail("retry limit reached");
    }

    private async Task RespectDelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        TimeSpan elapsed = now - _lastRequestUtc;

        if (elapsed < delay)
        {
            await Task.Delay(delay - elapsed, cancellationToken);
        }

        _lastRequestUtc = DateTimeOffset.UtcNow;
    }

    private static bool ShouldRetry(HttpStatusCode statusCode)
    {
        int code = (int)statusCode;

        return code is 408 or 429 or 500 or 502 or 503 or 504;
    }

    private static TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        RetryConditionHeaderValue? retryAfter = response.Headers.RetryAfter;

        if (retryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
        {
            return CapRetryDelay(delta);
        }

        if (retryAfter?.Date is { } date)
        {
            TimeSpan until = date - DateTimeOffset.UtcNow;

            if (until > TimeSpan.Zero)
            {
                return CapRetryDelay(until);
            }
        }

        return TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, attempt + 1)));
    }

    private static TimeSpan CapRetryDelay(TimeSpan delay)
    {
        if (delay > TimeSpan.FromMinutes(5))
        {
            return TimeSpan.FromMinutes(5);
        }

        return delay;
    }

    private static bool LooksLikeHtml(Uri url, string? mediaType)
    {
        if (mediaType is null)
        {
            return UrlTools.HasHtmlLikeExtension(url);
        }

        return mediaType.Contains("html", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> ReadLimitedStringAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken);
        using var memory = new MemoryStream();

        byte[] buffer = new byte[81920];
        int total = 0;

        while (true)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);

            if (read == 0)
            {
                break;
            }

            total += read;

            if (total > maxBytes)
            {
                throw new IOException($"response exceeded maximum page size of {maxBytes:N0} bytes");
            }

            memory.Write(buffer, 0, read);
        }

        string? charset = content.Headers.ContentType?.CharSet?.Trim('"');
        Encoding encoding = Encoding.UTF8;

        if (!string.IsNullOrWhiteSpace(charset))
        {
            try
            {
                encoding = Encoding.GetEncoding(charset);
            }
            catch
            {
                encoding = Encoding.UTF8;
            }
        }

        return encoding.GetString(memory.ToArray());
    }

    private sealed record FetchResult(bool Success, string? Html, string Message)
    {
        public static FetchResult Ok(string html) => new(true, html, "OK");

        public static FetchResult Fail(string message) => new(false, null, message);
    }
}

internal sealed record MediaWikiParsedArticle(string Title, string Html);

internal sealed record MediaWikiPage(Uri PageUrl, Uri RawUrl, Uri ApiParseUrl, string Title)
{
    private static readonly string[] KnownWikiHostSuffixes =
    [
        ".wikipedia.org",
        ".wiktionary.org",
        ".wikibooks.org",
        ".wikiquote.org",
        ".wikisource.org",
        ".wikiversity.org",
        ".wikivoyage.org",
        ".mediawiki.org"
    ];

    private static readonly string[] NonArticleNamespaces =
    [
        "Special",
        "File",
        "Image",
        "Talk",
        "User",
        "User talk",
        "Wikipedia",
        "Wikipedia talk",
        "Template",
        "Template talk",
        "Category",
        "Category talk",
        "Help",
        "Help talk",
        "Portal",
        "Portal talk",
        "Draft",
        "Draft talk",
        "Module",
        "Module talk",
        "MediaWiki",
        "TimedText"
    ];

    public static bool TryCreate(Uri url, out MediaWikiPage? page)
    {
        page = null;

        if (!LooksLikeMediaWiki(url) || !TryGetPageTitle(url, out string title))
        {
            return false;
        }

        if (IsNonArticleNamespace(title))
        {
            return false;
        }

        page = new MediaWikiPage(url, CreateRawUrl(url), CreateParseApiUrl(url, title), title);
        return true;
    }

    private static bool LooksLikeMediaWiki(Uri url)
    {
        string host = url.Host.ToLowerInvariant();

        if (KnownWikiHostSuffixes.Any(suffix => host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return host.Contains("wiki", StringComparison.OrdinalIgnoreCase) &&
            (url.AbsolutePath.Contains("/wiki/", StringComparison.OrdinalIgnoreCase) ||
             url.AbsolutePath.EndsWith("/index.php", StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryGetPageTitle(Uri url, out string title)
    {
        if (TryGetQueryValue(url.Query, "title", out string? queryTitle))
        {
            title = NormalizeTitle(queryTitle ?? "");
            return !string.IsNullOrWhiteSpace(title);
        }

        string path = url.AbsolutePath;
        int wikiIndex = path.IndexOf("/wiki/", StringComparison.OrdinalIgnoreCase);

        if (wikiIndex < 0)
        {
            title = "";
            return false;
        }

        string rawTitle = path[(wikiIndex + "/wiki/".Length)..].Trim('/');
        title = NormalizeTitle(rawTitle);
        return !string.IsNullOrWhiteSpace(title);
    }

    private static Uri CreateRawUrl(Uri url)
    {
        var builder = new UriBuilder(url)
        {
            Fragment = ""
        };

        List<string> queryParts = builder.Query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(static part => !part.StartsWith("action=", StringComparison.OrdinalIgnoreCase))
            .ToList();

        queryParts.Add("action=raw");
        builder.Query = string.Join("&", queryParts);

        return builder.Uri;
    }

    private static Uri CreateParseApiUrl(Uri url, string title)
    {
        var builder = new UriBuilder(url)
        {
            Path = "/w/api.php",
            Fragment = ""
        };

        string[] queryParts =
        [
            "action=parse",
            $"page={Uri.EscapeDataString(title.Replace(' ', '_'))}",
            "prop=text%7Cdisplaytitle",
            "format=json",
            "formatversion=2",
            "redirects=1",
            "disabletoc=1"
        ];

        builder.Query = string.Join("&", queryParts);
        return builder.Uri;
    }

    private static bool TryGetQueryValue(string query, string name, out string? value)
    {
        foreach (string part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pieces = part.Split('=', 2);
            string key = SafeUnescape(pieces[0]);

            if (!key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            value = pieces.Length > 1
                ? SafeUnescape(pieces[1])
                : "";

            return true;
        }

        value = null;
        return false;
    }

    private static bool IsNonArticleNamespace(string title)
    {
        int colonIndex = title.IndexOf(':');

        if (colonIndex <= 0)
        {
            return false;
        }

        string prefix = title[..colonIndex].Trim();
        return NonArticleNamespaces.Any(ns => ns.Equals(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeTitle(string rawTitle)
    {
        string decoded = SafeUnescape(rawTitle).Replace('_', ' ').Trim();
        return Regex.Replace(decoded, @"\s+", " ");
    }

    private static string SafeUnescape(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value.Replace('+', ' '));
        }
        catch
        {
            return value;
        }
    }
}

internal static class HtmlDiagnostics
{
    private const int DynamicShellTextThreshold = 1200;

    private static readonly Regex[] DynamicShellSignals =
    [
        new(@"id=[""']__(?:next|nuxt)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"id=[""'](?:root|app)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"data-reactroot", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"webpack", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"chunk[-_.]", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"<script[^>]+type=[""']module[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled)
    ];

    public static bool LooksLikeDynamicShell(string html)
    {
        if (GetVisibleTextLength(html) >= DynamicShellTextThreshold)
        {
            return false;
        }

        return DynamicShellSignals.Any(signal => signal.IsMatch(html));
    }

    private static int GetVisibleTextLength(string html)
    {
        string text = Regex.Replace(html, @"<script[\s\S]*?</script>", " ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<style[\s\S]*?</style>", " ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<[^>]+>", " ");
        text = Regex.Replace(text, @"\s+", " ").Trim();

        return text.Length;
    }
}

internal static class GeneratorDetector
{
    public static string Detect(IDocument document)
    {
        string? generator = document.QuerySelector("meta[name='generator']")?.GetAttribute("content");

        if (!string.IsNullOrWhiteSpace(generator))
        {
            string lower = generator.ToLowerInvariant();

            foreach (string key in new[] { "doxygen", "sphinx", "mkdocs", "gitbook", "jsdoc", "rustdoc" })
            {
                if (lower.Contains(key, StringComparison.OrdinalIgnoreCase))
                {
                    return key;
                }
            }
        }

        if (document.QuerySelector("#doxygen-nav, .doxygen, .contents") is not null)
        {
            return "doxygen";
        }

        if (document.QuerySelector(".sphinxsidebar, div.highlight, div[role='main']") is not null)
        {
            return "sphinx";
        }

        if (document.QuerySelector(".md-sidebar, .md-content") is not null)
        {
            return "mkdocs";
        }

        return "_default";
    }
}

internal sealed record ContentExtractionResult(
    string Html,
    IElement Content,
    string SourceLabel,
    double Score);

internal static class ContentExtraction
{
    private static readonly Regex[] BoilerplatePatterns =
    [
        new(@"cookie", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"privacy policy", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"terms of (?:service|use)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"all rights reserved", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"subscribe", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"newsletter", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"sign in", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"log in", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"accept all", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"manage preferences", RegexOptions.IgnoreCase | RegexOptions.Compiled)
    ];

    public static ContentExtractionResult ExtractMainHtml(IDocument document, Uri url, string generator)
    {
        ResolveUrls(document.DocumentElement ?? document.Body, url, document);

        IElement content = SelectMainContent(document, generator, out string sourceLabel, out double score)
            ?? document.Body
            ?? document.DocumentElement
            ?? throw new InvalidOperationException("HTML document has no root element.");

        if (generator == "mediawiki")
        {
            RemoveMediaWikiChrome(content);
        }

        RemoveNavigationChrome(content);
        RemoveHiddenAndBoilerplate(content);

        return new ContentExtractionResult(content.InnerHtml, content, sourceLabel, score);
    }

    public static string GetTitle(IDocument document, Uri url)
    {
        string? title =
            GetMetaContent(document, "og:title") ??
            GetMetaContent(document, "twitter:title") ??
            document.Title?.Trim();

        if (!string.IsNullOrWhiteSpace(title))
        {
            return NormalizeInlineText(title);
        }

        string? h1 = document.QuerySelector("h1")?.TextContent?.Trim();

        if (!string.IsNullOrWhiteSpace(h1))
        {
            return NormalizeInlineText(h1);
        }

        string last = url.AbsolutePath.Trim('/').Split('/').LastOrDefault() ?? url.Host;
        return string.IsNullOrWhiteSpace(last) ? url.Host : last;
    }

    private static IElement? SelectMainContent(IDocument document, string generator, out string sourceLabel, out double score)
    {
        var candidates = new List<(IElement Element, string Label)>();
        var seen = new HashSet<IElement>();

        foreach (string selector in GetContentSelectors(generator))
        {
            IElement? element = document.QuerySelector(selector);

            if (element is not null && seen.Add(element))
            {
                candidates.Add((element, selector));
            }
        }

        foreach (IElement element in document.QuerySelectorAll("article, main, [role='main'], section, div"))
        {
            if (seen.Add(element))
            {
                candidates.Add((element, GuessSelectorLabel(element)));
            }
        }

        (IElement Element, string Label, double Score)? best = null;

        foreach ((IElement element, string label) in candidates)
        {
            double candidateScore = ScoreCandidate(element);

            if (best is null || candidateScore > best.Value.Score)
            {
                best = (element, label, candidateScore);
            }
        }

        if (best is { } selected && selected.Score > 320)
        {
            sourceLabel = $"{selected.Label} score {selected.Score:0}";
            score = selected.Score;
            return selected.Element;
        }

        (IElement Element, string Label)? preferred = candidates.FirstOrDefault(candidate => ScoreCandidate(candidate.Element) > 80);

        if (preferred is { Element: not null })
        {
            score = ScoreCandidate(preferred.Value.Element);
            sourceLabel = $"{preferred.Value.Label} fallback";
            return preferred.Value.Element;
        }

        IElement? body = document.Body ?? document.DocumentElement;
        score = body is null ? 0 : ScoreCandidate(body);
        sourceLabel = "body fallback";
        return body;
    }

    private static double ScoreCandidate(IElement element)
    {
        string text = NormalizeInlineText(element.TextContent);
        int textLength = text.Length;

        if (textLength < 80)
        {
            return double.NegativeInfinity;
        }

        int linkTextLength = element
            .QuerySelectorAll("a")
            .Select(static a => NormalizeInlineText(a.TextContent).Length)
            .Sum();

        double linkDensity = textLength > 0
            ? (double)linkTextLength / textLength
            : 1;

        int paragraphCount = element.QuerySelectorAll("p").Length;
        int headingCount = element.QuerySelectorAll("h1,h2,h3").Length;
        int codeCount = element.QuerySelectorAll("pre, code").Length;
        int tableCount = element.QuerySelectorAll("table").Length;
        int listCount = element.QuerySelectorAll("li").Length;

        double score = textLength * (1 - Math.Min(linkDensity, 0.95));
        score += paragraphCount * 35;
        score += headingCount * 25;
        score += codeCount * 18;
        score += tableCount * 40;
        score -= listCount * 2;

        string tag = element.LocalName.ToLowerInvariant();

        if (tag is "article" or "main")
        {
            score *= 1.4;
        }

        string idClass = $"{element.Id} {element.ClassName}".ToLowerInvariant();

        if (Regex.IsMatch(idClass, @"article|content|main|post|entry|body|doc"))
        {
            score *= 1.2;
        }

        if (Regex.IsMatch(idClass, @"nav|menu|footer|header|sidebar|breadcrumb|toc"))
        {
            score *= 0.5;
        }

        return score;
    }

    private static string[] GetContentSelectors(string generator)
    {
        return generator switch
        {
            "doxygen" =>
            [
                "div.contents",
                "div#doc-content",
                "div.textblock",
                "main",
                "article"
            ],

            "sphinx" =>
            [
                "div[role='main']",
                "div.body",
                "article.bd-article",
                "main",
                "section"
            ],

            "mkdocs" =>
            [
                "div.md-content",
                "article.md-content__inner",
                "main"
            ],

            "gitbook" =>
            [
                "section.page-inner",
                "div.page-wrapper",
                "main"
            ],

            "mediawiki" =>
            [
                ".mw-parser-output",
                "main",
                "article",
                "body"
            ],

            _ =>
            [
                "main",
                "article",
                "div#content",
                "div.content",
                "div#main",
                "div.main",
                "body"
            ]
        };
    }

    private static void RemoveNavigationChrome(IElement content)
    {
        const string selector = """
            nav,
            .navtab,
            .tablist,
            .tabs,
            #nav-path,
            #MSearchBox,
            .searchresults,
            .breadcrumb,
            .breadcrumbs,
            .toc,
            .table-of-contents,
            .sidebar,
            .sphinxsidebar,
            .md-sidebar,
            .ad,
            .ads,
            .advertisement,
            .sponsored,
            .promo,
            .social-share,
            .share,
            .share-buttons,
            .cookie-banner,
            .cookie,
            .consent,
            .gdpr,
            .popup,
            .modal,
            .newsletter,
            .subscribe,
            .comments,
            #comments,
            .related-posts,
            .related,
            footer,
            aside,
            script,
            style,
            iframe,
            noscript
            """;

        foreach (IElement element in content.QuerySelectorAll(selector).ToArray())
        {
            element.Remove();
        }
    }

    private static void RemoveMediaWikiChrome(IElement content)
    {
        const string selector = """
            .mw-editsection,
            .mw-jump-link,
            .reference,
            sup.reference,
            .mw-references-wrap,
            .references,
            .reflist,
            .noprint,
            .metadata,
            .ambox,
            .hatnote,
            .shortdescription,
            .printfooter,
            .mw-empty-elt,
            .navbox,
            .vertical-navbox,
            .infobox,
            .sidebar,
            .sistersitebox,
            .authority-control,
            .portal,
            .gallery,
            .thumb,
            figure,
            figcaption,
            img,
            audio,
            video
            """;

        foreach (IElement element in content.QuerySelectorAll(selector).ToArray())
        {
            element.Remove();
        }

        foreach (IElement span in content.QuerySelectorAll("span").ToArray())
        {
            UnwrapElement(span);
        }

        RemoveMediaWikiTrailingSections(content);

        foreach (IElement element in content.QuerySelectorAll("p, li, section, div").ToArray())
        {
            if (string.IsNullOrWhiteSpace(NormalizeInlineText(element.TextContent)) &&
                !element.QuerySelectorAll("table, pre, code").Any())
            {
                element.Remove();
            }
        }
    }

    private static void UnwrapElement(IElement element)
    {
        INode? parent = element.Parent;

        if (parent is null)
        {
            return;
        }

        while (element.FirstChild is { } child)
        {
            parent.InsertBefore(child, element);
        }

        element.Remove();
    }

    private static void RemoveMediaWikiTrailingSections(IElement content)
    {
        var droppedSectionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Notes",
            "References",
            "Bibliography",
            "External links",
            "Further reading",
            "Sources",
            "Citations",
            "Footnotes",
            "Works cited"
        };

        bool dropping = false;

        foreach (IElement child in content.Children.ToArray())
        {
            if (child.LocalName.Equals("h2", StringComparison.OrdinalIgnoreCase))
            {
                string heading = GetMediaWikiHeadingText(child);
                dropping = droppedSectionNames.Contains(heading);
            }

            if (dropping)
            {
                child.Remove();
            }
        }
    }

    private static string GetMediaWikiHeadingText(IElement heading)
    {
        string text = heading.QuerySelector(".mw-headline")?.TextContent ?? heading.TextContent;
        text = Regex.Replace(text, @"\[\s*edit\s*\]", "", RegexOptions.IgnoreCase);
        return NormalizeInlineText(WebUtility.HtmlDecode(text));
    }

    private static void RemoveHiddenAndBoilerplate(IElement content)
    {
        foreach (IElement element in content.QuerySelectorAll("[hidden], [aria-hidden='true'], [style*='display: none'], [style*='display:none']").ToArray())
        {
            element.Remove();
        }

        foreach (IElement element in content.QuerySelectorAll("div, section, aside, p, li").ToArray())
        {
            string text = NormalizeInlineText(element.TextContent);

            if (text.Length is > 0 and <= 220 && BoilerplatePatterns.Any(pattern => pattern.IsMatch(text)))
            {
                element.Remove();
            }
        }
    }

    private static void ResolveUrls(IElement? root, Uri baseUrl, IDocument document)
    {
        if (root is null)
        {
            return;
        }

        Uri effectiveBase = GetEffectiveBaseUrl(document, baseUrl);

        foreach (IElement element in root.QuerySelectorAll("a[href], img, source, video, audio, link[rel='canonical']"))
        {
            foreach (string attribute in new[] { "href", "src", "poster", "data-src", "data-href", "data-original", "data-lazy-src" })
            {
                if (!element.HasAttribute(attribute))
                {
                    continue;
                }

                string? raw = element.GetAttribute(attribute);

                if (TryAbsolutize(raw, effectiveBase, out string? absolute))
                {
                    element.SetAttribute(attribute, absolute);

                    if (attribute is "data-src" or "data-original" or "data-lazy-src" &&
                        !element.HasAttribute("src"))
                    {
                        element.SetAttribute("src", absolute);
                    }

                    if (attribute == "data-href" && !element.HasAttribute("href"))
                    {
                        element.SetAttribute("href", absolute);
                    }
                }
            }

            if (element.HasAttribute("srcset"))
            {
                element.SetAttribute("srcset", NormalizeSrcSet(element.GetAttribute("srcset"), effectiveBase));
            }

            if (element.LocalName.Equals("img", StringComparison.OrdinalIgnoreCase) &&
                !element.HasAttribute("src") &&
                element.GetAttribute("srcset") is { Length: > 0 } srcset)
            {
                string firstCandidate = srcset.Split(',')[0].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

                if (!string.IsNullOrWhiteSpace(firstCandidate))
                {
                    element.SetAttribute("src", firstCandidate);
                }
            }

            if (element.LocalName.Equals("a", StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(element.TextContent) &&
                element.GetAttribute("href") is { Length: > 0 } href &&
                !href.StartsWith('#'))
            {
                element.TextContent = href;
            }
        }
    }

    private static Uri GetEffectiveBaseUrl(IDocument document, Uri baseUrl)
    {
        string? href = document.QuerySelector("base[href]")?.GetAttribute("href");

        return TryAbsolutize(href, baseUrl, out string? absolute) && Uri.TryCreate(absolute, UriKind.Absolute, out Uri? parsed)
            ? parsed
            : baseUrl;
    }

    private static string NormalizeSrcSet(string? srcset, Uri baseUrl)
    {
        if (string.IsNullOrWhiteSpace(srcset))
        {
            return "";
        }

        return string.Join(
            ", ",
            srcset.Split(',').Select(part =>
            {
                string[] pieces = part.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

                if (pieces.Length == 0)
                {
                    return "";
                }

                string url = pieces[0];
                string descriptor = string.Join(' ', pieces.Skip(1));

                return TryAbsolutize(url, baseUrl, out string? absolute)
                    ? string.IsNullOrWhiteSpace(descriptor) ? absolute : $"{absolute} {descriptor}"
                    : part.Trim();
            }).Where(static part => !string.IsNullOrWhiteSpace(part)));
    }

    private static bool TryAbsolutize(string? value, Uri baseUrl, out string? absolute)
    {
        absolute = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string trimmed = value.Trim();

        if (trimmed.StartsWith('#') ||
            trimmed.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("tel:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!Uri.TryCreate(baseUrl, trimmed, out Uri? uri))
        {
            return false;
        }

        absolute = EscapeUrlForMarkdown(uri.AbsoluteUri);
        return true;
    }

    private static string EscapeUrlForMarkdown(string url)
    {
        return url
            .Replace("(", "%28", StringComparison.Ordinal)
            .Replace(")", "%29", StringComparison.Ordinal);
    }

    private static string? GetMetaContent(IDocument document, string name)
    {
        return document
            .QuerySelector($"meta[name='{name}'], meta[property='{name}']")
            ?.GetAttribute("content")
            ?.Trim();
    }

    private static string GuessSelectorLabel(IElement element)
    {
        if (!string.IsNullOrWhiteSpace(element.Id))
        {
            return $"#{element.Id}";
        }

        string? firstClass = element.ClassList.FirstOrDefault();

        return !string.IsNullOrWhiteSpace(firstClass)
            ? $".{firstClass}"
            : element.LocalName.ToLowerInvariant();
    }

    private static string NormalizeInlineText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        return Regex.Replace(text.Replace('\u00a0', ' '), @"\s+", " ").Trim();
    }
}

internal static class LinkDiscovery
{
    private static readonly Regex[] CommonSkipPatterns =
    [
        new(@"(?:^|/)search(?:/|\.html|\?)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"(?:^|/)assets/", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"(?:^|/)_static/", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"(?:^|/)_sources/", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"(?:^|/)_images/", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"(?:^|/)404\.html$", RegexOptions.IgnoreCase | RegexOptions.Compiled)
    ];

    private static readonly Regex[] DoxygenSkipPatterns =
    [
        new(@"annotated\.html$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"classes\.html$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"functions.*\.html$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"variables.*\.html$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"typedefs.*\.html$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"enums.*\.html$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"enumvalues.*\.html$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"files\.html$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"globals.*\.html$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"members.*\.html$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"hierarchy\.html$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"inherits\.html$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"navtree", RegexOptions.IgnoreCase | RegexOptions.Compiled)
    ];

    public static IReadOnlyList<Uri> DiscoverLinks(
        IDocument document,
        IElement? preferredRoot,
        Uri currentUrl,
        Uri baseUrl,
        string generator,
        bool sameBasePathOnly)
    {
        var found = new List<Uri>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (preferredRoot is not null)
        {
            AddLinks(preferredRoot.QuerySelectorAll("a[href]"));
        }

        AddLinks(document.QuerySelectorAll("a[href]"));

        return found;

        void AddLinks(IEnumerable<IElement> anchors)
        {
            foreach (IElement a in anchors)
            {
                string? href = a.GetAttribute("href");

                if (string.IsNullOrWhiteSpace(href))
                {
                    continue;
                }

                href = href.Split('#')[0].Trim();

                if (string.IsNullOrWhiteSpace(href))
                {
                    continue;
                }

                if (href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
                    href.StartsWith("tel:", StringComparison.OrdinalIgnoreCase) ||
                    href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
                    href.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!Uri.TryCreate(currentUrl, href, out Uri? absolute))
                {
                    continue;
                }

                Uri normalized = UrlTools.Normalize(absolute);

                if (!UrlTools.IsSameOrigin(normalized, baseUrl))
                {
                    continue;
                }

                if (sameBasePathOnly && !UrlTools.IsUnderBasePath(normalized, baseUrl))
                {
                    continue;
                }

                if (!UrlTools.HasHtmlLikeExtension(normalized))
                {
                    continue;
                }

                if (ShouldSkip(normalized, generator))
                {
                    continue;
                }

                if (seen.Add(normalized.AbsoluteUri))
                {
                    found.Add(normalized);
                }
            }
        }
    }

    private static bool ShouldSkip(Uri url, string generator)
    {
        string text = url.PathAndQuery;

        if (CommonSkipPatterns.Any(pattern => pattern.IsMatch(text)))
        {
            return true;
        }

        if (generator == "doxygen" && DoxygenSkipPatterns.Any(pattern => pattern.IsMatch(text)))
        {
            return true;
        }

        string extension = Path.GetExtension(url.AbsolutePath).ToLowerInvariant();

        if (extension is ".css" or ".js" or ".png" or ".jpg" or ".jpeg" or ".gif" or ".svg" or ".webp" or ".ico" or ".pdf" or ".zip")
        {
            return true;
        }

        return false;
    }
}

internal static class UrlTools
{
    public static Uri Normalize(Uri uri)
    {
        var builder = new UriBuilder(uri)
        {
            Fragment = ""
        };

        builder.Host = builder.Host.ToLowerInvariant();

        return builder.Uri;
    }

    public static Uri GetBaseUrl(Uri startUrl)
    {
        string path = startUrl.AbsolutePath;

        if (!path.EndsWith('/'))
        {
            int lastSlash = path.LastIndexOf('/');

            path = lastSlash >= 0
                ? path[..(lastSlash + 1)]
                : "/";
        }

        var builder = new UriBuilder(startUrl)
        {
            Path = path,
            Query = "",
            Fragment = ""
        };

        return builder.Uri;
    }

    public static bool IsSameOrigin(Uri a, Uri b)
    {
        return a.Scheme.Equals(b.Scheme, StringComparison.OrdinalIgnoreCase)
            && a.Host.Equals(b.Host, StringComparison.OrdinalIgnoreCase)
            && a.Port == b.Port;
    }

    public static bool IsUnderBasePath(Uri candidate, Uri baseUrl)
    {
        string candidatePath = candidate.AbsolutePath;
        string basePath = baseUrl.AbsolutePath;

        if (!basePath.EndsWith('/'))
        {
            basePath += "/";
        }

        return candidatePath.StartsWith(basePath, StringComparison.OrdinalIgnoreCase);
    }

    public static bool HasHtmlLikeExtension(Uri url)
    {
        string path = url.AbsolutePath;

        if (path.EndsWith('/'))
        {
            return true;
        }

        string fileName = path.Split('/').LastOrDefault() ?? "";

        if (!fileName.Contains('.'))
        {
            return true;
        }

        string ext = Path.GetExtension(fileName).ToLowerInvariant();

        return ext is ".html" or ".htm" or ".php" or ".asp" or ".aspx";
    }
}

internal sealed class RobotsTxt
{
    public static RobotsTxt AllowAll { get; } = new(true, null, []);
    public static RobotsTxt DisallowAll { get; } = new(false, null, [new RobotRule(false, "/", int.MaxValue, RobotPattern.FromRobotsPattern("/"))]);

    private readonly bool _defaultAllow;
    private readonly List<RobotRule> _rules;

    public TimeSpan? CrawlDelay { get; }

    private RobotsTxt(bool defaultAllow, TimeSpan? crawlDelay, List<RobotRule> rules)
    {
        _defaultAllow = defaultAllow;
        CrawlDelay = crawlDelay;
        _rules = rules;
    }

    public bool IsAllowed(Uri uri)
    {
        if (_rules.Count == 0)
        {
            return _defaultAllow;
        }

        string pathAndQuery = uri.PathAndQuery;
        RobotRule? best = null;

        foreach (RobotRule rule in _rules)
        {
            if (rule.Pattern.IsMatch(pathAndQuery))
            {
                if (best is null ||
                    rule.PatternLength > best.PatternLength ||
                    rule.PatternLength == best.PatternLength && rule.Allow && !best.Allow)
                {
                    best = rule;
                }
            }
        }

        return best?.Allow ?? _defaultAllow;
    }

    public static RobotsTxt Parse(string text, string userAgent)
    {
        string product = userAgent.Split('/', ' ', '(')[0].Trim();

        var groups = new List<RobotGroup>();
        RobotGroup? current = null;
        bool sawRulesInCurrentGroup = false;

        foreach (string rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            string line = rawLine;
            int commentIndex = line.IndexOf('#');

            if (commentIndex >= 0)
            {
                line = line[..commentIndex];
            }

            line = line.Trim();

            if (line.Length == 0)
            {
                current = null;
                sawRulesInCurrentGroup = false;
                continue;
            }

            int colon = line.IndexOf(':');

            if (colon < 0)
            {
                continue;
            }

            string field = line[..colon].Trim().ToLowerInvariant();
            string value = line[(colon + 1)..].Trim();

            if (field == "user-agent")
            {
                if (current is null || sawRulesInCurrentGroup)
                {
                    current = new RobotGroup();
                    groups.Add(current);
                    sawRulesInCurrentGroup = false;
                }

                current.UserAgents.Add(value);
                continue;
            }

            if (current is null)
            {
                continue;
            }

            switch (field)
            {
                case "allow":
                    sawRulesInCurrentGroup = true;
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        current.Rules.Add(RobotRule.Create(true, value));
                    }
                    break;

                case "disallow":
                    sawRulesInCurrentGroup = true;
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        current.Rules.Add(RobotRule.Create(false, value));
                    }
                    break;

                case "crawl-delay":
                    sawRulesInCurrentGroup = true;
                    if (double.TryParse(value, out double seconds) && seconds >= 0)
                    {
                        current.CrawlDelay = TimeSpan.FromSeconds(seconds);
                    }
                    break;
            }
        }

        RobotGroup? match = SelectBestGroup(groups, product);

        if (match is null)
        {
            return AllowAll;
        }

        return new RobotsTxt(true, match.CrawlDelay, match.Rules);
    }

    private static RobotGroup? SelectBestGroup(List<RobotGroup> groups, string product)
    {
        RobotGroup? best = null;
        int bestLength = -1;

        foreach (RobotGroup group in groups)
        {
            foreach (string agent in group.UserAgents)
            {
                string trimmed = agent.Trim();

                bool match = trimmed == "*"
                    || product.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase);

                if (!match)
                {
                    continue;
                }

                int length = trimmed == "*" ? 0 : trimmed.Length;

                if (length > bestLength)
                {
                    best = group;
                    bestLength = length;
                }
            }
        }

        return best;
    }

    private sealed class RobotGroup
    {
        public List<string> UserAgents { get; } = [];
        public List<RobotRule> Rules { get; } = [];
        public TimeSpan? CrawlDelay { get; set; }
    }

    private sealed record RobotRule(bool Allow, string RawPattern, int PatternLength, RobotPattern Pattern)
    {
        public static RobotRule Create(bool allow, string rawPattern)
        {
            return new RobotRule(
                allow,
                rawPattern,
                rawPattern.Length,
                RobotPattern.FromRobotsPattern(rawPattern)
            );
        }
    }

    private sealed class RobotPattern
    {
        private readonly Regex _regex;

        private RobotPattern(Regex regex)
        {
            _regex = regex;
        }

        public bool IsMatch(string pathAndQuery)
        {
            return _regex.IsMatch(pathAndQuery);
        }

        public static RobotPattern FromRobotsPattern(string pattern)
        {
            string regex = Regex.Escape(pattern)
                .Replace(@"\*", ".*", StringComparison.Ordinal);

            if (regex.EndsWith(@"\$", StringComparison.Ordinal))
            {
                regex = regex[..^2] + "$";
            }
            else
            {
                regex += ".*";
            }

            regex = "^" + regex;

            return new RobotPattern(new Regex(regex, RegexOptions.Compiled));
        }
    }
}

internal static class MarkdownPostProcessor
{
    private static readonly HashSet<string> DroppedWikipediaMarkdownSections = new(StringComparer.OrdinalIgnoreCase)
    {
        "Notes",
        "References",
        "Bibliography",
        "External links",
        "Further reading",
        "Sources",
        "Citations",
        "Footnotes",
        "Works cited"
    };

    private static readonly (string Bad, string Good)[] TextArtifactReplacements =
    [
        ("Â©", "©"),
        ("Â®", "®"),
        ("Â°", "°"),
        ("Â·", "·"),
        ("Â", ""),
        ("â€™", "’"),
        ("â€˜", "‘"),
        ("â€œ", "“"),
        ("â€�", "”"),
        ("â€“", "–"),
        ("â€”", "—"),
        ("â€¦", "…"),
        ("â€¢", "•"),
        ("â„¢", "™"),
        ("Ã—", "×"),
        ("Ã·", "÷")
    ];

    public static string Clean(string markdown, Uri? wikiPageUrl = null)
    {
        markdown = NormalizeTextArtifacts(markdown);
        markdown = Regex.Replace(markdown, @"```\{=html\}.*?```", "", RegexOptions.Singleline);
        markdown = RemoveImageArtifacts(markdown);
        markdown = RemoveHtmlContainerArtifacts(markdown);
        markdown = RemoveInlineHtmlArtifacts(markdown);
        markdown = ConvertSimpleHtmlAnchors(markdown);
        markdown = ResolveWikiMarkdownLinks(markdown, wikiPageUrl);
        markdown = RemoveDroppedWikipediaSections(markdown);
        markdown = RemoveEmptyReferenceArtifacts(markdown);
        markdown = RemoveFootnoteReferences(markdown);
        markdown = Regex.Replace(markdown, @"\[\s*([^\]\r\n]+?)\s*\]\(([^\r\n()]*)\)", "[$1]($2)");
        markdown = markdown.Replace(")[", ")\n[", StringComparison.Ordinal);
        markdown = Regex.Replace(markdown, @"\[([^\]]*)\]\(//([^\)]*)\)", "[$1](https://$2)");
        markdown = Regex.Replace(markdown, @"\n{3,}", "\n\n");
        markdown = DedupeBoilerplate(markdown);
        markdown = Regex.Replace(markdown, @"\n{3,}", "\n\n");
        return markdown.Trim();
    }

    private static string NormalizeTextArtifacts(string text)
    {
        foreach ((string bad, string good) in TextArtifactReplacements)
        {
            text = text.Replace(bad, good, StringComparison.Ordinal);
        }

        return text;
    }

    private static string RemoveImageArtifacts(string markdown)
    {
        string[] lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var cleaned = new List<string>(lines.Length);
        bool inCodeFence = false;

        foreach (string line in lines)
        {
            string trimmed = line.Trim();

            if (trimmed.StartsWith("```", StringComparison.Ordinal) ||
                trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                inCodeFence = !inCodeFence;
                cleaned.Add(line);
                continue;
            }

            if (inCodeFence)
            {
                cleaned.Add(line);
                continue;
            }

            if (Regex.IsMatch(trimmed, @"^<img\b[^>]*>\s*$", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(trimmed, @"^<figcaption\b[^>]*>.*?</figcaption>\s*$", RegexOptions.IgnoreCase))
            {
                continue;
            }

            string current = Regex.Replace(line, @"<img\b[^>]*>", "", RegexOptions.IgnoreCase);
            current = Regex.Replace(current, @"<figcaption\b[^>]*>.*?</figcaption>", "", RegexOptions.IgnoreCase);
            current = RemoveMarkdownImagesFromLine(current);

            if (string.IsNullOrWhiteSpace(current) && !string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            cleaned.Add(current);
        }

        return string.Join('\n', cleaned);
    }

    private static string RemoveMarkdownImagesFromLine(string line)
    {
        var builder = new StringBuilder(line.Length);
        int index = 0;

        while (index < line.Length)
        {
            int imageStart = line.IndexOf("![", index, StringComparison.Ordinal);

            if (imageStart < 0)
            {
                builder.Append(line, index, line.Length - index);
                break;
            }

            builder.Append(line, index, imageStart - index);

            if (TryFindMarkdownInlineEnd(line, imageStart + 1, out int imageEnd))
            {
                index = imageEnd + 1;
                continue;
            }

            builder.Append(line[imageStart]);
            index = imageStart + 1;
        }

        return Regex.Replace(builder.ToString(), @"\s{2,}", " ").TrimStart();
    }

    private static string RemoveInlineHtmlArtifacts(string markdown)
    {
        string[] lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var cleaned = new List<string>(lines.Length);
        bool inCodeFence = false;

        foreach (string line in lines)
        {
            string trimmed = line.Trim();

            if (trimmed.StartsWith("```", StringComparison.Ordinal) ||
                trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                inCodeFence = !inCodeFence;
                cleaned.Add(line);
                continue;
            }

            if (inCodeFence)
            {
                cleaned.Add(line);
                continue;
            }

            string current = Regex.Replace(line, @"<span\b[^>]*>\s*</span>", "", RegexOptions.IgnoreCase);
            current = Regex.Replace(current, @"<span\b[^>]*>(.*?)</span>", "$1", RegexOptions.IgnoreCase);
            current = Regex.Replace(current, @"</?span\b[^>]*>", "", RegexOptions.IgnoreCase);
            cleaned.Add(current);
        }

        return string.Join('\n', cleaned);
    }

    private static string ConvertSimpleHtmlAnchors(string markdown)
    {
        return Regex.Replace(
            markdown,
            @"<a\b[^>]*href\s*=\s*[""'](?<href>[^""']+)[""'][^>]*>(?<text>.*?)</a>",
            match =>
            {
                string text = Regex.Replace(match.Groups["text"].Value, @"<[^>]+>", " ");
                text = WebUtility.HtmlDecode(Regex.Replace(text, @"\s+", " ").Trim());
                string href = WebUtility.HtmlDecode(match.Groups["href"].Value.Trim());

                if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(href))
                {
                    return text;
                }

                return $"[{EscapeMarkdownLinkText(text)}]({EscapeMarkdownLinkDestination(href)})";
            },
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
    }

    private static string ResolveWikiMarkdownLinks(string markdown, Uri? wikiPageUrl)
    {
        if (wikiPageUrl is null)
        {
            return markdown;
        }

        string[] lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var resolved = new List<string>(lines.Length);
        bool inCodeFence = false;

        foreach (string line in lines)
        {
            string trimmed = line.Trim();

            if (trimmed.StartsWith("```", StringComparison.Ordinal) ||
                trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                inCodeFence = !inCodeFence;
                resolved.Add(line);
                continue;
            }

            resolved.Add(inCodeFence ? line : ResolveWikiMarkdownLinksInLine(line, wikiPageUrl));
        }

        return string.Join('\n', resolved);
    }

    private static string ResolveWikiMarkdownLinksInLine(string line, Uri wikiPageUrl)
    {
        var builder = new StringBuilder(line.Length);
        int index = 0;

        while (index < line.Length)
        {
            int linkStart = line.IndexOf('[', index);

            if (linkStart < 0)
            {
                builder.Append(line, index, line.Length - index);
                break;
            }

            if (linkStart > 0 && line[linkStart - 1] == '!')
            {
                builder.Append(line, index, linkStart - index + 1);
                index = linkStart + 1;
                continue;
            }

            builder.Append(line, index, linkStart - index);

            if (!TryReadMarkdownLink(line, linkStart, out string label, out string destination, out int linkEnd))
            {
                builder.Append(line[linkStart]);
                index = linkStart + 1;
                continue;
            }

            string resolvedDestination = ResolveWikiLinkDestination(destination, wikiPageUrl);
            builder.Append('[').Append(label).Append("](").Append(resolvedDestination).Append(')');
            index = linkEnd + 1;
        }

        return builder.ToString();
    }

    private static string ResolveWikiLinkDestination(string destination, Uri wikiPageUrl)
    {
        string trimmed = destination.Trim();

        if (trimmed.Length == 0 ||
            trimmed.StartsWith('#') ||
            trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("tel:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        string escaped = NormalizeWikiLinkTarget(trimmed);

        return Uri.TryCreate(wikiPageUrl, escaped, out Uri? absolute)
            ? EscapeMarkdownLinkDestination(absolute.AbsoluteUri)
            : EscapeMarkdownLinkDestination(trimmed);
    }

    private static string NormalizeWikiLinkTarget(string destination)
    {
        string target = destination.Trim().Replace(' ', '_');

        try
        {
            target = Uri.UnescapeDataString(target);
        }
        catch
        {
            // Keep the original target if a malformed escape sequence came from Pandoc.
        }

        return target
            .Replace("(", "%28", StringComparison.Ordinal)
            .Replace(")", "%29", StringComparison.Ordinal);
    }

    private static string RemoveDroppedWikipediaSections(string markdown)
    {
        string[] lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var kept = new List<string>(lines.Length);
        int droppedHeadingLevel = 0;
        bool inCodeFence = false;

        foreach (string line in lines)
        {
            string trimmed = line.Trim();

            if (trimmed.StartsWith("```", StringComparison.Ordinal) ||
                trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                inCodeFence = !inCodeFence;
                kept.Add(line);
                continue;
            }

            if (!inCodeFence &&
                TryParseMarkdownHeading(trimmed, out int level, out string headingText))
            {
                if (droppedHeadingLevel > 0 && level <= droppedHeadingLevel)
                {
                    droppedHeadingLevel = 0;
                }

                if (DroppedWikipediaMarkdownSections.Contains(headingText))
                {
                    droppedHeadingLevel = level;
                    continue;
                }
            }

            if (droppedHeadingLevel > 0 && !inCodeFence)
            {
                continue;
            }

            kept.Add(line);
        }

        return string.Join('\n', kept);
    }

    private static string RemoveEmptyReferenceArtifacts(string markdown)
    {
        markdown = Regex.Replace(markdown, @"(?m)^\s*\[\^\d+\]:\s*$", "");
        markdown = Regex.Replace(markdown, @"(?m)^\s*-\s*$", "");
        markdown = Regex.Replace(markdown, @"(?m)^\s*(?:\[[^\]]+\]\((?:Category:|/wiki/Category:)[^)]+\)\s*)+$", "");
        markdown = Regex.Replace(markdown, @"\(\s*\)", "");
        return markdown;
    }

    private static string RemoveFootnoteReferences(string markdown)
    {
        markdown = Regex.Replace(markdown, @"\[\^\d+\]", "");
        markdown = Regex.Replace(markdown, @"\s+([,.;:!?])", "$1");
        markdown = Regex.Replace(markdown, @" {2,}", " ");
        return markdown;
    }

    private static string RemoveHtmlContainerArtifacts(string markdown)
    {
        string[] lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var cleaned = new List<string>(lines.Length);
        bool inCodeFence = false;

        foreach (string line in lines)
        {
            string trimmed = line.Trim();

            if (trimmed.StartsWith("```", StringComparison.Ordinal) ||
                trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                inCodeFence = !inCodeFence;
                cleaned.Add(line);
                continue;
            }

            if (inCodeFence)
            {
                cleaned.Add(line);
                continue;
            }

            if (Regex.IsMatch(trimmed, @"^</?div(?:\s+[^>]*)?>$", RegexOptions.IgnoreCase))
            {
                continue;
            }

            string withoutTrailingCloseDiv = Regex.Replace(line, @"\s*</div>\s*$", "", RegexOptions.IgnoreCase);

            if (withoutTrailingCloseDiv.Length == 0 && line.Length > 0)
            {
                continue;
            }

            cleaned.Add(withoutTrailingCloseDiv);
        }

        return string.Join('\n', cleaned);
    }

    private static bool TryParseMarkdownHeading(string line, out int level, out string headingText)
    {
        Match match = Regex.Match(line, @"^(?<hashes>#{1,6})\s+(?<text>.+?)(?:\s+#+)?$");

        if (!match.Success)
        {
            level = 0;
            headingText = "";
            return false;
        }

        level = match.Groups["hashes"].Value.Length;
        headingText = WebUtility.HtmlDecode(match.Groups["text"].Value.Trim());
        headingText = Regex.Replace(headingText, @"\s+", " ");
        return true;
    }

    private static bool TryReadMarkdownLink(
        string text,
        int linkStart,
        out string label,
        out string destination,
        out int linkEnd)
    {
        label = "";
        destination = "";
        linkEnd = -1;

        if (!TryFindClosingBracket(text, linkStart, out int labelEnd) ||
            labelEnd + 1 >= text.Length ||
            text[labelEnd + 1] != '(')
        {
            return false;
        }

        int destinationStart = labelEnd + 2;

        if (!TryFindMarkdownDestinationEnd(text, destinationStart, out int destinationEnd))
        {
            return false;
        }

        label = text[(linkStart + 1)..labelEnd];
        destination = text[destinationStart..destinationEnd];
        linkEnd = destinationEnd;
        return true;
    }

    private static bool TryFindMarkdownInlineEnd(string text, int labelStart, out int inlineEnd)
    {
        inlineEnd = -1;

        if (!TryFindClosingBracket(text, labelStart, out int labelEnd) ||
            labelEnd + 1 >= text.Length ||
            text[labelEnd + 1] != '(')
        {
            return false;
        }

        int destinationStart = labelEnd + 2;
        return TryFindMarkdownDestinationEnd(text, destinationStart, out inlineEnd);
    }

    private static bool TryFindClosingBracket(string text, int bracketStart, out int bracketEnd)
    {
        bracketEnd = -1;
        int depth = 0;

        for (int i = bracketStart; i < text.Length; i++)
        {
            if (text[i] == '\\')
            {
                i++;
                continue;
            }

            if (text[i] == '[')
            {
                depth++;
                continue;
            }

            if (text[i] != ']')
            {
                continue;
            }

            depth--;

            if (depth == 0)
            {
                bracketEnd = i;
                return true;
            }
        }

        return false;
    }

    private static bool TryFindMarkdownDestinationEnd(string text, int destinationStart, out int destinationEnd)
    {
        destinationEnd = -1;
        bool inSingleQuote = false;
        bool inDoubleQuote = false;

        for (int i = destinationStart; i < text.Length; i++)
        {
            char current = text[i];

            if (current == '\\')
            {
                i++;
                continue;
            }

            if (current == '\'' && !inDoubleQuote && (inSingleQuote || i == destinationStart || char.IsWhiteSpace(text[i - 1])))
            {
                inSingleQuote = !inSingleQuote;
                continue;
            }

            if (current == '"' && !inSingleQuote && (inDoubleQuote || i == destinationStart || char.IsWhiteSpace(text[i - 1])))
            {
                inDoubleQuote = !inDoubleQuote;
                continue;
            }

            if (current != ')' || inSingleQuote || inDoubleQuote)
            {
                continue;
            }

            if (i + 1 == text.Length || char.IsWhiteSpace(text[i + 1]) || IsMarkdownLinkBoundary(text[i + 1]))
            {
                destinationEnd = i;
                return true;
            }
        }

        return false;
    }

    private static bool IsMarkdownLinkBoundary(char value)
    {
        return value is '.' or ',' or ';' or ':' or '!' or '?' or ')' or ']' or '*' or '_' or '"' or '\'';
    }

    private static string EscapeMarkdownLinkText(string text)
    {
        return text.Replace("[", "\\[", StringComparison.Ordinal).Replace("]", "\\]", StringComparison.Ordinal);
    }

    private static string EscapeMarkdownLinkDestination(string href)
    {
        return href.Replace(" ", "%20", StringComparison.Ordinal).Replace(")", "%29", StringComparison.Ordinal);
    }

    private static string DedupeBoilerplate(string markdown)
    {
        string[] lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var deduped = new List<string>();
        string? previous = null;
        bool inCodeFence = false;

        foreach (string line in lines)
        {
            string trimmed = line.Trim();

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                inCodeFence = !inCodeFence;
                deduped.Add(line);
                previous = line;
                continue;
            }

            if (!inCodeFence && line == previous && trimmed.Length > 0)
            {
                continue;
            }

            deduped.Add(line);
            previous = line;
        }

        var frequencies = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (string line in deduped)
        {
            string key = line.Trim();

            if (key.Length is < 8 or > 140 ||
                key.StartsWith('#') ||
                key.StartsWith("```", StringComparison.Ordinal) ||
                key.StartsWith('|'))
            {
                continue;
            }

            frequencies[key] = frequencies.TryGetValue(key, out int count)
                ? count + 1
                : 1;
        }

        return string.Join(
            '\n',
            deduped.Where(line =>
            {
                string key = line.Trim();
                return key.Length == 0 ||
                    !frequencies.TryGetValue(key, out int count) ||
                    count < 3;
            }));
    }
}

internal static class MarkdownRenderer
{
    public static string Render(
        Uri startUrl,
        CrawlResult result,
        string userAgent,
        CrawlerOptions options)
    {
        var sb = new StringBuilder();

        for (int i = 0; i < result.Pages.Count; i++)
        {
            CrawledPage page = result.Pages[i];

            if (i > 0)
            {
                sb.AppendLine();
                sb.AppendLine("---");
                sb.AppendLine();
            }

            sb.AppendLine($"# {EscapeMarkdownHeading(page.Title)}");
            sb.AppendLine();
            sb.AppendLine(page.Markdown);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string EscapeMarkdownHeading(string value)
    {
        return value.Replace("\r", " ").Replace("\n", " ").Trim();
    }
}
