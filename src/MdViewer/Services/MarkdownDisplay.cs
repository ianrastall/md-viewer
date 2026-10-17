using System.Text;
using System.Text.RegularExpressions;
using MdViewer.Interop.Markdown;

namespace MdViewer.Services;

/// <summary>The searchable text drawn by the native renderer, without generated UI labels.</summary>
internal static partial class MarkdownDisplay
{
    [GeneratedRegex(@"^<br\s*/?>$", RegexOptions.IgnoreCase)]
    private static partial Regex BreakTag();

    public static bool IsBreakTag(string text) => BreakTag().IsMatch(text.Trim());
    public static string AltText(MarkdownInline image) => image.PlainText() is { Length: > 0 } alt ? $"[{alt}]" : "[image]";

    public static bool IsStandaloneImage(MarkdownBlock block)
    {
        var visible = block.Inlines.Where(i => i.Kind != InlineKind.SoftBreak && !(i.Kind == InlineKind.Text && string.IsNullOrWhiteSpace(i.Text))).ToList();
        return visible is [{ Kind: InlineKind.Image }];
    }

    public static IEnumerable<MarkdownInline> HeadingInlines(List<MarkdownInline> inlines)
    {
        if (inlines is not [.., { Kind: InlineKind.Text } last]) return inlines;
        var text = last.Text.TrimEnd();
        var open = text.LastIndexOf('{');
        if (!text.EndsWith('}') || open < 0 || open + 1 >= text.Length || !(text[open + 1] is '#' or '.' or '-' || text.IndexOf('=', open) > 0)) return inlines;
        return [.. inlines.Take(inlines.Count - 1), new MarkdownInline(InlineKind.Text) { Text = text[..open].TrimEnd() }];
    }

    public static IEnumerable<string> Segments(MarkdownBlock root)
    {
        var pending = new Stack<MarkdownBlock>();
        pending.Push(root);
        while (pending.TryPop(out var block))
        {
            switch (block.Kind)
            {
                case BlockKind.Heading:
                    yield return InlineText(HeadingInlines(block.Inlines));
                    break;
                case BlockKind.Paragraph when IsStandaloneImage(block): break;
                case BlockKind.Paragraph or BlockKind.TableHeaderCell or BlockKind.TableCell:
                    yield return InlineText(block.Inlines);
                    break;
                case BlockKind.Html when block.IsHtmlComment: break;
                case BlockKind.Code or BlockKind.Html or BlockKind.FrontMatter:
                    yield return block.Literal.TrimEnd('\n', '\r');
                    break;
                default:
                    for (var i = block.Blocks.Count - 1; i >= 0; i--) pending.Push(block.Blocks[i]);
                    break;
            }
        }
    }

    public static string InlineText(IEnumerable<MarkdownInline> inlines)
    {
        var text = new StringBuilder();
        Append(text, inlines, false, null, null);
        return text.ToString();
    }

    public static InlineProjection ProjectInlines(IEnumerable<MarkdownInline> inlines, IReadOnlySet<MarkdownInline>? imageObjects)
    {
        var text = new StringBuilder();
        var gaps = new List<(int Position, int Delta)>();
        Append(text, inlines, false, imageObjects, gaps);
        return new(text.ToString(), gaps);
    }

    // Images delimit searchable text. At rendering time, map the marker to zero
    // text characters for an image object, or to its longer fallback label, without resolving files
    // or loading network images while constructing a search index.
    private static int Append(StringBuilder text, IEnumerable<MarkdownInline> inlines, bool inLink,
        IReadOnlySet<MarkdownInline>? imageObjects, List<(int Position, int Delta)>? gaps)
    {
        var count = 0;
        foreach (var inline in inlines)
        {
            switch (inline.Kind)
            {
                case InlineKind.Text or InlineKind.Code:
                    text.Append(inline.Text); count++; break;
                case InlineKind.SoftBreak:
                    text.Append(' '); count++; break;
                case InlineKind.LineBreak:
                    text.Append('\r'); count++; break;
                case InlineKind.Html:
                    if (IsBreakTag(inline.Text)) { text.Append('\r'); count++; }
                    break;
                case InlineKind.Image:
                    if (gaps is not null)
                        gaps.Add((text.Length, (imageObjects?.Contains(inline) ?? false) ? -1 : AltText(inline).Length - 1));
                    text.Append('\uFFFC'); count++;
                    break;
                case InlineKind.Link when inLink:
                    count += Append(text, inline.Children, true, imageObjects, gaps);
                    break;
                case InlineKind.Link:
                    if (Append(text, inline.Children, true, imageObjects, gaps) == 0) text.Append(inline.Url);
                    count++;
                    break;
                case InlineKind.Emphasis or InlineKind.Strong or InlineKind.Strikethrough or InlineKind.Underline:
                    Append(text, inline.Children, inLink, imageObjects, gaps);
                    count++;
                    break;
            }
        }
        return count;
    }
}

internal sealed record InlineProjection(string Text, IReadOnlyList<(int Position, int Delta)> ImageGaps)
{
    public int DisplayOffset(int offset)
    {
        var result = offset;
        foreach (var (position, delta) in ImageGaps) if (position < offset) result += delta;
        return result;
    }
}
