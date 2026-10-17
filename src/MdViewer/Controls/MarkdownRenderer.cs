using MdViewer.Interop.Markdown;
using MdViewer.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI.Text;

namespace MdViewer.Controls;

/// <summary>Builds native WinUI elements for the C++ core's document model. No browser or WebView is involved.</summary>
internal sealed class MarkdownRenderer(double zoom, string? baseDirectory, Action<string> linkInvoked,
    SearchPresentation? search = null, MarkdownBlock? activeBlock = null)
{
    private static readonly FontFamily BodyFont = new("Segoe UI Variable Text, Segoe UI");
    private static readonly FontFamily DisplayFont = new("Segoe UI Variable Display, Segoe UI");
    private static readonly FontFamily MonoFont = new("Cascadia Mono, Cascadia Code, Consolas");
    private static readonly FontFamily SymbolFont = new("Segoe UI Symbol");
    private static readonly double[] HeadingSizes = [30, 24, 20, 17.5, 16, 15];
    private static readonly string[] Bullets = ["•", "◦", "▪"];

    private MarkdownBlock? currentBlock;
    private int textIndex, inlineOffset;
    private HashSet<MarkdownInline>? imageObjects;
    private readonly List<(Run Run, int Start)> runPositions = [];
    public FrameworkElement? ActiveSearchElement { get; private set; }
    public TextPointer? ActiveSearchPointer { get; private set; }

    private double S(double size) => Math.Round(size * zoom, 1);
    private double BodySize => S(15);
    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    /// <summary>A top-level block with the vertical rhythm of the page.</summary>
    public FrameworkElement Build(MarkdownBlock block, bool first)
    {
        currentBlock = block;
        textIndex = 0;
        if (ReferenceEquals(block, activeBlock)) { ActiveSearchElement = null; ActiveSearchPointer = null; }
        var element = Block(block, 0, quiet: false);
        if (element.Visibility == Visibility.Collapsed) return element;
        element.Margin = block.Kind == BlockKind.Heading
            ? new Thickness(0, first ? 0 : S(block.Level <= 2 ? 30 : 22), 0, S(10))
            : new Thickness(0, 0, 0, S(16));
        return element;
    }

    private FrameworkElement Block(MarkdownBlock block, int listDepth, bool quiet) => block.Kind switch
    {
        BlockKind.Paragraph => Paragraph(block, quiet),
        BlockKind.Heading => Heading(block),
        BlockKind.Code => CodeBlock(block.Literal, block.Language.StartsWith("{=", StringComparison.Ordinal) ? "raw " + block.Language.Trim('{', '}', '=') : block.Language, quiet: false),
        BlockKind.FrontMatter => CodeBlock(block.Literal, "front matter", quiet: true),
        BlockKind.Html when block.IsHtmlComment => new Border { Visibility = Visibility.Collapsed },
        BlockKind.Html => CodeBlock(block.Literal, "html", quiet: true),
        BlockKind.Quote => new Border
        {
            BorderBrush = Brush("QuoteBarBrush"),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(S(18), S(2), 0, S(2)),
            Child = Stack(block.Blocks, listDepth, quiet: true, S(12))
        },
        BlockKind.BulletList or BlockKind.OrderedList => List(block, listDepth, quiet),
        BlockKind.ThematicBreak => new Border { Height = 1, Background = Brush("LineBrush"), Margin = new Thickness(0, S(10), 0, S(10)) },
        BlockKind.Table => Table(block, quiet),
        _ => Stack(block.Blocks, listDepth, quiet, S(12))
    };

    private StackPanel Stack(IEnumerable<MarkdownBlock> blocks, int listDepth, bool quiet, double spacing)
    {
        var panel = new StackPanel { Spacing = spacing };
        foreach (var child in blocks)
        {
            var element = Block(child, listDepth, quiet);
            if (element.Visibility == Visibility.Visible) panel.Children.Add(element);
        }
        return panel;
    }

    private FrameworkElement Paragraph(MarkdownBlock block, bool quiet)
    {
        // A paragraph holding only an image (the usual screenshot or figure) becomes a block-level
        // image, so it can use the column's full width.
        var standalone = MarkdownDisplay.IsStandaloneImage(block);
        if (standalone && ImageElement(block.Inlines.First(i => i.Kind == InlineKind.Image), inline: false) is { } picture) return picture;
        return Text(block.Inlines, BodySize, 1.6, quiet ? Brush("MutedBrush") : Brush("TextBrush"), searchable: !standalone);
    }

    private FrameworkElement Heading(MarkdownBlock block)
    {
        var level = Math.Clamp(block.Level, 1, 6);
        var text = Text(MarkdownDisplay.HeadingInlines(block.Inlines), S(HeadingSizes[level - 1]), 1.3, level == 6 ? Brush("MutedBrush") : Brush("TextBrush"), DisplayFont);
        text.FontWeight = level == 1 ? FontWeights.Bold : FontWeights.SemiBold;
        if (level > 2) return text;
        var panel = new StackPanel { Spacing = S(8) };
        panel.Children.Add(text);
        panel.Children.Add(new Border { Height = 1, Background = Brush("LineBrush") });
        return panel;
    }

    private FrameworkElement CodeBlock(string code, string label, bool quiet)
    {
        var literal = code.TrimEnd('\n', '\r');
        var codeRun = new Run { Text = literal };
        var codeText = new TextBlock
        {
            FontFamily = MonoFont, FontSize = S(13.5), LineHeight = S(20), IsTextSelectionEnabled = true,
            Foreground = quiet ? Brush("MutedBrush") : Brush("CodeTextBrush"), Padding = new Thickness(0, 0, 0, S(4))
        };
        codeText.Inlines.Add(codeRun);
        var index = textIndex++;
        Highlight(codeText.TextHighlighters, literal, index);
        if (ActiveMatch(index) is { } match)
        {
            ActiveSearchElement = codeText;
            ActiveSearchPointer = codeRun.ContentStart.GetPositionAtOffset(match.Start, LogicalDirection.Forward);
        }
        var panel = new StackPanel { Spacing = S(6) };
        if (label.Length > 0)
            panel.Children.Add(new TextBlock { Text = label.ToUpperInvariant(), FontSize = S(10.5), CharacterSpacing = 80, Foreground = Brush("QuietBrush") });
        panel.Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollMode = ScrollMode.Disabled,
            Content = codeText
        });
        return new Border
        {
            Background = Brush("CodeSurfaceBrush"),
            BorderBrush = Brush("LineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(S(16), S(12), S(16), S(10)),
            Child = panel
        };
    }

    private StackPanel List(MarkdownBlock list, int depth, bool quiet)
    {
        var panel = new StackPanel { Spacing = list.IsTight ? S(4) : S(10) };
        var number = list.Start;
        foreach (var item in list.Blocks)
        {
            var marker = new TextBlock
            {
                FontSize = BodySize,
                LineHeight = BodySize * 1.6,
                MinWidth = S(20),
                TextAlignment = TextAlignment.Right,
                Foreground = Brush("MutedBrush"),
                Text = list.Kind == BlockKind.OrderedList ? $"{number++}." : Bullets[depth % Bullets.Length]
            };
            if (item.Task != TaskState.None)
            {
                marker.Text = item.Task == TaskState.Done ? "☑" : "☐";
                marker.FontFamily = SymbolFont;
                marker.Foreground = item.Task == TaskState.Done ? Brush("AccentBrush") : Brush("MutedBrush");
            }
            var content = Stack(item.Blocks, depth + 1, quiet, S(8));
            var row = new Grid { ColumnSpacing = S(10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(marker);
            Grid.SetColumn(content, 1);
            row.Children.Add(content);
            panel.Children.Add(row);
        }
        return panel;
    }

    private ScrollViewer Table(MarkdownBlock table, bool quiet)
    {
        var rows = table.Blocks.SelectMany(section => section.Blocks.Select(row => (Row: row, Header: section.Kind == BlockKind.TableHead))).ToList();
        var columns = rows.Count == 0 ? 0 : rows.Max(r => r.Row.Blocks.Count);
        var grid = new Grid();
        for (var c = 0; c < columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (var r = 0; r < rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var c = 0; c < rows[r].Row.Blocks.Count; c++)
            {
                var cell = rows[r].Row.Blocks[c];
                var text = Text(cell.Inlines, S(14), 1.5, quiet ? Brush("MutedBrush") : Brush("TextBrush"));
                text.MaxWidth = S(520);
                text.TextAlignment = cell.Alignment switch { CellAlignment.Center => TextAlignment.Center, CellAlignment.Right => TextAlignment.Right, _ => TextAlignment.Left };
                if (rows[r].Header) text.FontWeight = FontWeights.SemiBold;
                var border = new Border
                {
                    BorderBrush = Brush("LineBrush"),
                    BorderThickness = new Thickness(c == 0 ? 1 : 0, r == 0 ? 1 : 0, 1, 1),
                    Background = rows[r].Header ? Brush("SurfaceBrush") : r % 2 == 0 ? Brush("SidebarBrush") : null,
                    Padding = new Thickness(S(12), S(7), S(12), S(7)),
                    Child = text
                };
                Grid.SetRow(border, r);
                Grid.SetColumn(border, c);
                grid.Children.Add(border);
            }
        }
        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollMode = ScrollMode.Disabled,
            HorizontalAlignment = HorizontalAlignment.Left,
            Content = grid
        };
    }

    private RichTextBlock Text(IEnumerable<MarkdownInline> inlines, double size, double lineSpacing, Brush foreground, FontFamily? font = null, bool searchable = true)
    {
        IEnumerable<MarkdownInline> content = search is { Query.Length: > 0 } ? inlines.ToList() : inlines;
        imageObjects = null;
        runPositions.Clear();
        inlineOffset = 0;
        var paragraph = new Paragraph();
        AddInlines(paragraph.Inlines, content, size, inLink: false);
        var text = new RichTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            FontFamily = font ?? BodyFont,
            FontSize = size,
            LineHeight = Math.Round(size * lineSpacing),
            Foreground = foreground,
            SelectionHighlightColor = (SolidColorBrush)Brush("QuoteBarBrush")
        };
        text.Blocks.Add(paragraph);
        if (searchable)
        {
            var index = textIndex++;
            if (search is { Query.Length: > 0 })
            {
                var projection = MarkdownDisplay.ProjectInlines(content, imageObjects);
                Highlight(text.TextHighlighters, projection.Text, index, projection.DisplayOffset);
                if (ActiveMatch(index) is { } match)
                {
                    ActiveSearchElement = text;
                    var start = projection.DisplayOffset(match.Start);
                    var run = runPositions.FirstOrDefault(r => start >= r.Start && start < r.Start + r.Run.Text.Length);
                    if (run.Run is not null) ActiveSearchPointer = run.Run.ContentStart.GetPositionAtOffset(start - run.Start, LogicalDirection.Forward);
                }
            }
        }
        return text;
    }

    private SearchMatch? ActiveMatch(int index) => ReferenceEquals(currentBlock, activeBlock) && search?.Match is { } match && match.TextIndex == index ? match : null;

    private void Highlight(IList<TextHighlighter> target, string text, int index, Func<int, int>? displayOffset = null)
    {
        if (search is not { Query.Length: > 0 }) return;
        var active = ActiveMatch(index);
        var ordinary = new TextHighlighter { Background = Brush("LineBrush"), Foreground = Brush("TextBrush") };
        var comparison = search.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var found = text.IndexOf(search.Query, comparison);
        // Keep ranges bounded even for one enormous code block. The selected
        // occurrence is always highlighted, including beyond this visible-mark limit.
        for (var count = 0; found >= 0 && count < 2000; count++, found = text.IndexOf(search.Query, found + 1, comparison))
            if (active is null || found + search.Query.Length <= active.Start || found >= active.Start + active.Length)
                Add(ordinary, found, search.Query.Length);
        if (ordinary.Ranges.Count > 0) target.Add(ordinary);
        if (active is not null)
        {
            var selected = new TextHighlighter { Background = Brush("AccentBrush"), Foreground = Brush("BackgroundBrush") };
            Add(selected, active.Start, active.Length);
            target.Add(selected);
        }

        void Add(TextHighlighter highlighter, int start, int length)
        {
            var first = displayOffset?.Invoke(start) ?? start;
            var end = displayOffset?.Invoke(start + length) ?? start + length;
            if (end > first) highlighter.Ranges.Add(new Microsoft.UI.Xaml.Documents.TextRange { StartIndex = first, Length = end - first });
        }
    }

    private void AddRun(InlineCollection target, Run run)
    {
        target.Add(run);
        if (search is { Query.Length: > 0 }) runPositions.Add((run, inlineOffset));
        inlineOffset += run.Text.Length;
    }

    private void AddInlines(InlineCollection target, IEnumerable<MarkdownInline> inlines, double size, bool inLink)
    {
        foreach (var inline in inlines)
        {
            switch (inline.Kind)
            {
                case InlineKind.Text: AddRun(target, new Run { Text = inline.Text }); break;
                case InlineKind.SoftBreak: AddRun(target, new Run { Text = " " }); break;
                case InlineKind.LineBreak: target.Add(new LineBreak()); inlineOffset++; break;
                case InlineKind.Emphasis: target.Add(Wrap(new Italic(), inline, size, inLink)); break;
                case InlineKind.Strong: target.Add(Wrap(new Bold(), inline, size, inLink)); break;
                case InlineKind.Underline: target.Add(Wrap(new Underline(), inline, size, inLink)); break;
                case InlineKind.Strikethrough: target.Add(Wrap(new Span { TextDecorations = TextDecorations.Strikethrough }, inline, size, inLink)); break;
                case InlineKind.Code:
                    AddRun(target, new Run { Text = inline.Text, FontFamily = MonoFont, FontSize = Math.Round(size * 0.9, 1), Foreground = Brush("CodeTextBrush") });
                    break;
                case InlineKind.Html:
                    if (MarkdownDisplay.IsBreakTag(inline.Text)) { target.Add(new LineBreak()); inlineOffset++; }
                    break;
                case InlineKind.Image:
                    if (!inLink && ImageElement(inline, inline: true) is { } image)
                    {
                        target.Add(new InlineUIContainer { Child = image });
                        if (search is { Query.Length: > 0 }) (imageObjects ??= []).Add(inline);
                    }
                    else AddRun(target, new Run { Text = MarkdownDisplay.AltText(inline), Foreground = Brush("MutedBrush") });
                    break;
                case InlineKind.Link when inLink:
                    AddInlines(target, inline.Children, size, inLink);
                    break;
                case InlineKind.Link:
                {
                    var link = new Hyperlink { Foreground = Brush("LinkBrush"), UnderlineStyle = UnderlineStyle.Single };
                    AddInlines(link.Inlines, inline.Children, size, inLink: true);
                    if (link.Inlines.Count == 0) AddRun(link.Inlines, new Run { Text = inline.Url });
                    var url = inline.Url;
                    link.Click += (_, _) => linkInvoked(url);
                    ToolTipService.SetToolTip(link, string.IsNullOrEmpty(inline.Title) ? url : $"{inline.Title}\n{url}");
                    target.Add(link);
                    break;
                }
            }
        }
    }

    private Span Wrap(Span span, MarkdownInline inline, double size, bool inLink)
    {
        AddInlines(span.Inlines, inline.Children, size, inLink);
        return span;
    }

    private FrameworkElement? ImageElement(MarkdownInline image, bool inline)
    {
        if (ResolveImage(image.Url) is not { } uri) return null;
        var element = new Image { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, MaxHeight = S(1400) };
        if (inline) element.Height = S(20);
        if (uri.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
        {
            // SVG has no pixel size to honor: inline ones are usually badges, standalone ones figures.
            var svg = new SvgImageSource(uri);
            if (!inline) element.MaxWidth = S(720);
            svg.OpenFailed += (_, _) => element.Visibility = Visibility.Collapsed;
            element.Source = svg;
        }
        else
        {
            var bitmap = new BitmapImage(uri);
            // Standalone images retain natural size; inline images share a stable text-line height.
            bitmap.ImageOpened += (_, _) => element.MaxWidth = bitmap.PixelWidth * zoom;
            bitmap.ImageFailed += (_, _) => element.Visibility = Visibility.Collapsed;
            element.Source = bitmap;
        }
        var alt = image.PlainText();
        if (alt.Length > 0) Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(element, alt);
        if ((image.Title.Length > 0 ? image.Title : alt) is { Length: > 0 } tip) ToolTipService.SetToolTip(element, tip);
        return element;
    }

    private Uri? ResolveImage(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute))
        {
            if (absolute.Scheme is "http" or "https") return absolute;
            return absolute.IsFile && File.Exists(absolute.LocalPath) ? absolute : null;
        }
        if (baseDirectory is null) return null;
        var relative = url.Split('#', '?')[0];
        try
        {
            var path = Path.GetFullPath(Path.Combine(baseDirectory, Uri.UnescapeDataString(relative).Replace('/', Path.DirectorySeparatorChar)));
            return File.Exists(path) ? new Uri(path) : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or UriFormatException) { return null; }
    }

}
