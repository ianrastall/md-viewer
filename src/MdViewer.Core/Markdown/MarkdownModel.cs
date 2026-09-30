namespace MdViewer.Core.Markdown;

// Values match md4c's MD_BLOCKTYPE, plus the native core's front-matter block.
public enum BlockKind : byte
{
    Quote = 1, BulletList = 2, OrderedList = 3, ListItem = 4, ThematicBreak = 5, Heading = 6,
    Code = 7, Html = 8, Paragraph = 9, Table = 10, TableHead = 11, TableBody = 12, TableRow = 13,
    TableHeaderCell = 14, TableCell = 15, FrontMatter = 100
}

public enum InlineKind { Text, SoftBreak, LineBreak, Emphasis, Strong, Strikethrough, Underline, Code, Link, Image, Html }
public enum TaskState : byte { None, Open, Done }
public enum CellAlignment : byte { Default, Left, Center, Right }

public sealed class MarkdownBlock(BlockKind kind)
{
    public BlockKind Kind { get; } = kind;
    public List<MarkdownBlock> Blocks { get; } = [];
    public List<MarkdownInline> Inlines { get; } = [];
    public int Level { get; set; }
    public string Language { get; set; } = "";
    public bool IsTight { get; set; }
    public int Start { get; set; } = 1;
    public TaskState Task { get; set; }
    public CellAlignment Alignment { get; set; }
    /// <summary>Verbatim text of code, HTML, and front-matter blocks.</summary>
    public string Literal { get; set; } = "";
    public bool HoldsInlines => Kind is BlockKind.Paragraph or BlockKind.Heading or BlockKind.TableHeaderCell or BlockKind.TableCell;
    public bool HoldsLiteral => Kind is BlockKind.Code or BlockKind.Html or BlockKind.FrontMatter;
    public bool IsHtmlComment => Kind == BlockKind.Html && Literal.Trim() is var t && t.StartsWith("<!--", StringComparison.Ordinal) && t.EndsWith("-->", StringComparison.Ordinal);
}

public sealed class MarkdownInline(InlineKind kind)
{
    public InlineKind Kind { get; } = kind;
    public string Text { get; set; } = "";
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public List<MarkdownInline> Children { get; } = [];

    public string PlainText() => Kind switch
    {
        InlineKind.Text or InlineKind.Code => Text,
        InlineKind.SoftBreak or InlineKind.LineBreak => " ",
        InlineKind.Html => "",
        _ => string.Concat(Children.Select(c => c.PlainText()))
    };
}

public sealed record HeadingInfo(int Level, int Line, int BlockIndex, string Title, string Slug);

public sealed class MarkdownDocument
{
    public required IReadOnlyList<MarkdownBlock> Blocks { get; init; }
    public required IReadOnlyList<HeadingInfo> Headings { get; init; }
    public int LineCount { get; init; }
    public int WordCount { get; init; }
    public int CharacterCount { get; init; }
}

public sealed record ReflowResult(string Markdown, int ChangedHeadingCount, int HeadingCount, IReadOnlyList<string> Warnings);

public sealed class NativeCoreException(string message) : Exception(message);
