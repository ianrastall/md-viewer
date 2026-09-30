using System.Text;
using MdViewer.Core.Crawl;
using MdViewer.Core.Documents;
using MdViewer.Core.Import;
using MdViewer.Core.Markdown;
using MdViewer.Core.Pandoc;

// Integration checks for the C++ core and the managed services. Exits nonzero on failure.
var failures = 0;
var checks = 0;
void Check(string name, bool condition, string detail = "")
{
    checks++;
    if (condition) return;
    failures++;
    Console.WriteLine($"FAIL  {name}{(detail.Length > 0 ? " — " + detail : "")}");
}
static MarkdownBlock Only(MarkdownDocument d, BlockKind kind) => d.Blocks.Single(b => b.Kind == kind);

var scratch = Directory.CreateTempSubdirectory("md-viewer-tests-");
try
{
    // ---- Parsing -----------------------------------------------------------
    var doc = MarkdownEngine.Parse("# Title\n\nSome *emphasis*, **strong**, `code`, ~~gone~~ and a [link](https://example.com \"tip\").\n\n## Second & more\n\n```csharp\nvar x = 1;\n```\n");
    Check("block count", doc.Blocks.Count == 4, $"{doc.Blocks.Count}");
    Check("headings", doc.Headings.Count == 2 && doc.Headings[1].Title == "Second & more" && doc.Headings[1].Level == 2);
    Check("heading lines", doc.Headings[0].Line == 1 && doc.Headings[1].Line == 5, $"{doc.Headings[0].Line},{doc.Headings[1].Line}");
    Check("heading blocks", doc.Headings[0].BlockIndex == 0 && doc.Headings[1].BlockIndex == 2);
    Check("heading slugs", doc.Headings[0].Slug == "title" && doc.Headings[1].Slug == "second--more", doc.Headings[1].Slug);
    var paragraph = doc.Blocks[1];
    Check("inline kinds", paragraph.Inlines.Select(i => i.Kind).Distinct().Count() >= 6, string.Join(",", paragraph.Inlines.Select(i => i.Kind)));
    var link = paragraph.Inlines.Single(i => i.Kind == InlineKind.Link);
    Check("link", link.Url == "https://example.com" && link.Title == "tip" && link.PlainText() == "link");
    Check("code span", paragraph.Inlines.Single(i => i.Kind == InlineKind.Code).Text == "code");
    var code = doc.Blocks[3];
    Check("code block", code.Kind == BlockKind.Code && code.Language == "csharp" && code.Literal == "var x = 1;\n", code.Literal);
    Check("line count", doc.LineCount == 10, $"{doc.LineCount}");
    Check("word count", doc.WordCount == 14, $"{doc.WordCount}");

    var stats = MarkdownEngine.Parse("a\r\nb\rc\n😀");
    Check("mixed line endings", stats.LineCount == 4, $"{stats.LineCount}");
    Check("utf-16 characters", stats.CharacterCount == "a\r\nb\rc\n😀".Length, $"{stats.CharacterCount}");
    Check("empty document", MarkdownEngine.Parse("") is { LineCount: 1, Blocks.Count: 0, WordCount: 0 });

    var entities = MarkdownEngine.Parse("&copy; &#169; &#x1F600; &bogus; a\0b");
    Check("entities", entities.Blocks[0].Inlines[0].Text == "© © 😀 &bogus; a�b", entities.Blocks[0].Inlines[0].Text);

    var tight = MarkdownEngine.Parse("- [x] done\n- [ ] open\n- plain\n\n3. three\n4. four\n");
    var bullets = tight.Blocks[0];
    Check("task list", bullets.Kind == BlockKind.BulletList && bullets.IsTight && bullets.Blocks[0].Task == TaskState.Done && bullets.Blocks[1].Task == TaskState.Open && bullets.Blocks[2].Task == TaskState.None);
    Check("tight items get paragraphs", bullets.Blocks.All(item => item.Blocks.Count == 1 && item.Blocks[0].Kind == BlockKind.Paragraph));
    Check("ordered start", tight.Blocks[1] is { Kind: BlockKind.OrderedList, Start: 3 });

    var table = MarkdownEngine.Parse("| a | b |\n|:--|--:|\n| 1 | 2 |\n");
    var head = Only(table, BlockKind.Table).Blocks[0];
    Check("table", head.Kind == BlockKind.TableHead && head.Blocks[0].Blocks[1] is { Kind: BlockKind.TableHeaderCell, Alignment: CellAlignment.Right });

    var front = MarkdownEngine.Parse("---\ntitle: x\n---\n\n# After\n");
    Check("front matter", front.Blocks[0].Kind == BlockKind.FrontMatter && front.Blocks[0].Literal.Contains("title: x") && front.Headings.Single().Line == 5 && front.Headings[0].BlockIndex == 1);
    Check("thematic break is not front matter", MarkdownEngine.Parse("---\n\ntext\n").Blocks[0].Kind == BlockKind.ThematicBreak);

    var comment = MarkdownEngine.Parse("<!--\nPDF import: 3 page(s)\n-->\n\ntext\n");
    Check("html comment block", comment.Blocks[0].IsHtmlComment);
    var fenced = MarkdownEngine.Parse("```\n# not a heading\n```\n\n~~~\n## nor this\n~~~\n");
    Check("fenced code hides headings", fenced.Headings.Count == 0);
    var duplicate = MarkdownEngine.Parse("# Intro\n# Intro\n## Title {#custom .class}\n");
    Check("duplicate slugs", duplicate.Headings[1].Slug == "intro-1");
    Check("pandoc attributes stripped", duplicate.Headings[2].Title == "Title");
    Check("setext heading", MarkdownEngine.Parse("Title\n=====\n").Headings.Single() is { Level: 1, Title: "Title", Line: 1 });
    Check("quoted heading", MarkdownEngine.Parse("> ## Inside\n").Headings.Single() is { Level: 2, BlockIndex: 0 });

    var big = new StringBuilder();
    for (var i = 0; i < 20000; i++) big.Append("## Section ").Append(i).Append("\n\nParagraph with **bold** text and a [link](#section-").Append(i).Append(").\n\n");
    var timer = System.Diagnostics.Stopwatch.StartNew();
    var large = MarkdownEngine.Parse(big.ToString());
    Check("large document", large.Headings.Count == 20000 && large.Blocks.Count == 40000);
    Console.WriteLine($"      parsed {big.Length / 1024} KB in {timer.ElapsedMilliseconds} ms");

    // ---- Heading reflow ---------------------------------------------------
    var reflow = MarkdownEngine.ReflowHeadings("# A\n\n### B\n\n### C\n\n##### D\n\ntext\n");
    Check("reflow levels", reflow.Markdown == "# A\n\n## B\n\n## C\n\n### D\n\ntext\n", reflow.Markdown);
    Check("reflow counts", reflow is { ChangedHeadingCount: 3, HeadingCount: 4 });
    var promoted = MarkdownEngine.ReflowHeadings("### Only\r\n\r\nbody ## not heading\r\n");
    Check("reflow promotes and keeps CRLF", promoted.Markdown == "# Only\r\n\r\nbody ## not heading\r\n", promoted.Markdown);
    var setext = MarkdownEngine.ReflowHeadings("# Top\n\nSub\n---\n\n#### Deep\n");
    Check("reflow setext unchanged when level fits", setext.Markdown == "# Top\n\nSub\n---\n\n### Deep\n", setext.Markdown);
    var setextChange = MarkdownEngine.ReflowHeadings("Lead\n----\n\nLine one\nline two\n---------\n");
    Check("reflow converts setext", setextChange.Markdown == "# Lead\n\n# Line one line two\n", setextChange.Markdown);
    var nested = MarkdownEngine.ReflowHeadings("# A\n\n> ### Quoted *heading* ###\n\n- #### In a list\n");
    Check("reflow in containers", nested.Markdown == "# A\n\n> ## Quoted *heading* ###\n\n- ### In a list\n", nested.Markdown);
    var metadata = MarkdownEngine.ReflowHeadings("---\ntitle: T\n---\n\n## Body\n");
    Check("reflow ignores front matter", metadata.Markdown == "---\ntitle: T\n---\n\n# Body\n" && metadata.ChangedHeadingCount == 1, metadata.Markdown);
    Check("reflow code fences", MarkdownEngine.ReflowHeadings("```\n### x\n```\n").ChangedHeadingCount == 0);
    var warnings = MarkdownEngine.ReflowHeadings("# T\n\n## U\n\n<h2>x</h2>\n\n[jump](#t)\n").Warnings;
    Check("reflow warnings", warnings.Count == 3 && warnings.Any(w => w.Contains("HTML heading")) && warnings.Any(w => w.Contains("Anchor")) && warnings.Any(w => w.Contains("first H1")), string.Join(" | ", warnings));
    Check("reflow no headings", MarkdownEngine.ReflowHeadings("plain").Warnings.Single().StartsWith("No Markdown headings"));
    Check("reflow keeps NUL", MarkdownEngine.ReflowHeadings("## a\0b\n").Markdown == "# a\0b\n");

    // ---- Files and encodings ----------------------------------------------
    var ansi = Path.Combine(scratch.FullName, "ansi.md");
    File.WriteAllBytes(ansi, [(byte)'c', (byte)'a', (byte)'f', 0xE9]);
    var ansiFile = await DocumentFile.ReadAsync(ansi);
    Check("windows-1252 detection", ansiFile is { Text: "café", EncodingLabel: "Windows-1252" });
    await DocumentFile.WriteAsync(ansi, "caffè", ansiFile.Encoding);
    Check("windows-1252 round trip", File.ReadAllBytes(ansi).SequenceEqual(new byte[] { (byte)'c', (byte)'a', (byte)'f', (byte)'f', 0xE8 }));
    Check("legacy encoding falls back to UTF-8", DocumentFile.EncodingFor("Σ", ansiFile.Encoding, ansiFile.EncodingLabel).Label == "UTF-8");
    var bom = Path.Combine(scratch.FullName, "bom.md");
    File.WriteAllBytes(bom, [0xEF, 0xBB, 0xBF, (byte)'#', (byte)' ', (byte)'x']);
    var bomFile = await DocumentFile.ReadAsync(bom);
    Check("utf-8 bom", bomFile is { Text: "# x", EncodingLabel: "UTF-8 BOM" });
    await DocumentFile.WriteAsync(bom, "# y", bomFile.Encoding);
    Check("utf-8 bom preserved", File.ReadAllBytes(bom).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'#', (byte)' ', (byte)'y' }));
    Check("no temporary files left", Directory.GetFiles(scratch.FullName, "*.tmp").Length == 0);

    // ---- PDF import (embedded text) ----------------------------------------
    var pdf = Path.Combine(scratch.FullName, "sample.pdf");
    File.WriteAllBytes(pdf, MinimalPdf("Imported text from a small PDF document for md-viewer verification purposes only."));
    var imported = await new PdfImportService().ImportAsMarkdownAsync(pdf);
    Check("pdf import", imported.PageCount == 1 && imported.Markdown.Contains("# sample") && imported.Markdown.Contains("md-viewer verification"), imported.Markdown);

    // ---- Crawler helpers ----------------------------------------------------
    var robots = RobotsTxt.Parse("User-agent: *\nDisallow: /private\nAllow: /private/ok\nCrawl-delay: 3\n", "md-viewer/2.0");
    Check("robots rules", !robots.IsAllowed(new Uri("https://x.test/private/a")) && robots.IsAllowed(new Uri("https://x.test/private/ok/b")) && robots.CrawlDelay == TimeSpan.FromSeconds(3));
    Check("markdown cleanup", MarkdownPostProcessor.Clean("Text[^1] here .\n\n<div>\n\n![img](a.png)\n") == "Text here.");

    // ---- Pandoc (optional) --------------------------------------------------
    Check("metadata split", PandocService.SplitMetadataBlock("---\na: 1\n---\n\nbody") == ("---\na: 1\n---", "body"));
    if (PandocLocator.Find() is { } pandocPath)
    {
        var pandoc = new PandocService();
        var formatted = await pandoc.FormatAsync("---\ntitle: T\n---\nSetext\n======\n\n| a | b |\n|---|---|\n| 1 | 2 |\n");
        Check("pandoc format", formatted.StartsWith("---\ntitle: T\n---\n\n# Setext", StringComparison.Ordinal) && formatted.Contains("| a"), formatted);
        var html = Path.Combine(scratch.FullName, "page.html");
        await File.WriteAllTextAsync(html, "<html><body><h1 id=\"x\" class=\"c\">Hello</h1><div class=\"note\"><p>World <span class=\"k\">here</span></p></div></body></html>");
        var fromHtml = await pandoc.ImportAsync(html);
        Check("pandoc import html", fromHtml.Contains("# Hello") && !fromHtml.Contains("{#") && !fromHtml.Contains(":::") && fromHtml.Contains("World here"), fromHtml);
        var export = Path.Combine(scratch.FullName, "out.html");
        await pandoc.ExportAsync("# Out\n\ntext\n", export, scratch.FullName);
        Check("pandoc export", File.ReadAllText(export).Contains("<h1"));
        Console.WriteLine($"      pandoc: {pandocPath}");
    }
    else Console.WriteLine("      pandoc not found; skipped Pandoc checks");

    // Opt-in: one polite live crawl (robots.txt plus a single MediaWiki API request).
    if (Environment.GetEnvironmentVariable("MDV_NETWORK_TESTS") == "1" && PandocLocator.Find() is not null)
    {
        var options = new CrawlerOptions
        {
            DefaultDelay = TimeSpan.FromSeconds(2), RequestTimeout = TimeSpan.FromSeconds(30), MaxPages = 1,
            MaxPageBytes = 5 * 1024 * 1024, MaxRetries = 2, SameBasePathOnly = true, RespectRobotsTxt = true
        };
        var log = new List<string>();
        var crawled = await new CrawlUrlService().CrawlAsync(new Uri("https://en.wikipedia.org/wiki/Markdown"), options, new SyncProgress(log.Add), CancellationToken.None);
        var parsed = MarkdownEngine.Parse(crawled);
        Check("live wikipedia crawl", crawled.StartsWith("# Markdown", StringComparison.Ordinal) && crawled.Contains("John Gruber") && !crawled.Contains(":::") && !crawled.Contains("<span") && parsed.Headings.Count > 3,
            crawled.Length > 600 ? crawled[..600] : crawled);
        Console.WriteLine($"      crawled {crawled.Length:N0} chars, {parsed.Headings.Count} headings: {string.Join(" | ", log.Where(l => l.StartsWith("WIKI") || l.StartsWith("OK")))}");
    }
}
finally { scratch.Delete(recursive: true); }

Console.WriteLine(failures == 0 ? $"PASS  {checks} core checks" : $"{failures} of {checks} core checks failed");
return failures == 0 ? 0 : 1;

static byte[] MinimalPdf(string text)
{
    var objects = new[]
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
        null,
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
    };
    var stream = $"BT /F1 12 Tf 72 720 Td ({text}) Tj ET";
    objects[3] = $"<< /Length {stream.Length} >>\nstream\n{stream}\nendstream";
    var pdf = new StringBuilder("%PDF-1.4\n");
    var offsets = new List<int>();
    for (var i = 0; i < objects.Length; i++)
    {
        offsets.Add(pdf.Length);
        pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
    }
    var xref = pdf.Length;
    pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
    foreach (var offset in offsets) pdf.Append($"{offset:D10} 00000 n \n");
    pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    return Encoding.ASCII.GetBytes(pdf.ToString());
}

sealed class SyncProgress(Action<string> report) : IProgress<string>
{
    public void Report(string value) => report(value);
}
