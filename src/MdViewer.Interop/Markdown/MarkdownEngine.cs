using System.Buffers.Binary;
using System.Text;

namespace MdViewer.Interop.Markdown;

/// <summary>Parsing and heading reflow, done by the C++ core (native/src/markdown.cpp).</summary>
public static unsafe class MarkdownEngine
{
    public static MarkdownDocument Parse(string markdown) => Native.Call(() =>
        Native.WithText(markdown, (text, length) => Native.Take(Native.Parse((byte*)text, length), static payload => Read(payload))));

    public static ReflowResult ReflowHeadings(string markdown) => Native.Call(() =>
    {
        var text = Native.WithText(markdown, (utf8, length) => Native.Text(Native.Reflow((byte*)utf8, length)));
        var headerEnd = text.IndexOf('\n');
        var counts = text[..headerEnd].Split('\t').Select(int.Parse).ToArray();
        var warnings = new List<string>();
        var position = headerEnd + 1;
        for (var i = 0; i < counts[2]; i++)
        {
            var end = text.IndexOf('\n', position);
            warnings.Add(text[position..end]);
            position = end + 1;
        }
        return new ReflowResult(text[position..], counts[0], counts[1], warnings);
    });

    private static MarkdownDocument Read(ReadOnlySpan<byte> data)
    {
        var reader = new ModelReader(data);
        var magic = Encoding.ASCII.GetString(data[..4]);
        reader.Skip(4);
        if (magic != "MDV1") throw new NativeCoreException("The C++ Markdown core returned an unrecognized document model.");

        var lines = reader.Int();
        var words = reader.Int();
        var characters = reader.Int();
        var headings = new HeadingInfo[reader.Int()];
        for (var i = 0; i < headings.Length; i++)
            headings[i] = new(reader.Byte(), reader.Int(), reader.Int(), reader.String(), reader.String());

        var roots = new List<MarkdownBlock>(reader.Int());
        var blocks = new Stack<MarkdownBlock>();
        var spans = new Stack<MarkdownInline>();
        StringBuilder? literal = null;
        MarkdownBlock? implicitParagraph = null;

        for (var op = reader.Byte(); op != 0; op = reader.Byte())
        {
            switch (op)
            {
                case 1:
                {
                    var block = new MarkdownBlock((BlockKind)reader.Byte());
                    switch (block.Kind)
                    {
                        case BlockKind.Heading: block.Level = reader.Byte(); break;
                        case BlockKind.Code: block.Language = reader.String(); break;
                        case BlockKind.BulletList: block.IsTight = reader.Byte() != 0; break;
                        case BlockKind.OrderedList: block.IsTight = reader.Byte() != 0; block.Start = reader.Int(); break;
                        case BlockKind.ListItem: block.Task = (TaskState)reader.Byte(); break;
                        case BlockKind.TableHeaderCell or BlockKind.TableCell: block.Alignment = (CellAlignment)reader.Byte(); break;
                    }
                    (blocks.Count == 0 ? roots : blocks.Peek().Blocks).Add(block);
                    blocks.Push(block);
                    implicitParagraph = null;
                    if (block.HoldsLiteral) literal = new StringBuilder();
                    break;
                }
                case 2:
                {
                    var block = blocks.Pop();
                    if (block.HoldsLiteral && literal is not null) { block.Literal = literal.ToString(); literal = null; }
                    implicitParagraph = null;
                    break;
                }
                case 3:
                {
                    var type = reader.Byte();
                    var inline = type switch
                    {
                        0 => new MarkdownInline(InlineKind.Emphasis),
                        1 => new MarkdownInline(InlineKind.Strong),
                        2 => new MarkdownInline(InlineKind.Link) { Url = reader.String(), Title = reader.String() },
                        3 => new MarkdownInline(InlineKind.Image) { Url = reader.String(), Title = reader.String() },
                        4 => new MarkdownInline(InlineKind.Code),
                        5 => new MarkdownInline(InlineKind.Strikethrough),
                        9 => new MarkdownInline(InlineKind.Underline),
                        _ => throw new NativeCoreException($"Unsupported span type {type} in the document model.")
                    };
                    Add(inline);
                    spans.Push(inline);
                    break;
                }
                case 4: spans.Pop(); break;
                case 5:
                {
                    var type = reader.Byte();
                    var text = reader.String();
                    if (literal is not null) { literal.Append(text); break; }
                    if (spans.TryPeek(out var span) && span.Kind == InlineKind.Code) { span.Text += text; break; }
                    switch (type)
                    {
                        case 2: Add(new MarkdownInline(InlineKind.LineBreak)); break;
                        case 3: Add(new MarkdownInline(InlineKind.SoftBreak)); break;
                        case 6: Add(new MarkdownInline(InlineKind.Html) { Text = text }); break;
                        default:
                            var target = Target();
                            if (target.Count > 0 && target[^1].Kind == InlineKind.Text) target[^1].Text += text;
                            else target.Add(new MarkdownInline(InlineKind.Text) { Text = text });
                            break;
                    }
                    break;
                }
                default: throw new NativeCoreException($"Corrupt document model (opcode {op}).");
            }
        }

        return new MarkdownDocument { Blocks = roots, Headings = headings, LineCount = lines, WordCount = words, CharacterCount = characters };

        void Add(MarkdownInline inline) => Target().Add(inline);

        // Tight list items carry inline content without a paragraph; give it one.
        List<MarkdownInline> Target()
        {
            if (spans.Count > 0) return spans.Peek().Children;
            var block = blocks.Peek();
            if (block.HoldsInlines) return block.Inlines;
            if (implicitParagraph is null)
            {
                implicitParagraph = new MarkdownBlock(BlockKind.Paragraph);
                block.Blocks.Add(implicitParagraph);
            }
            return implicitParagraph.Inlines;
        }
    }

    private ref struct ModelReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> data = data;
        private int position;

        public void Skip(int count) => position += count;
        public byte Byte() => data[position++];
        public int Int()
        {
            var value = BinaryPrimitives.ReadUInt32LittleEndian(data[position..]);
            position += 4;
            return checked((int)value);
        }
        public string String()
        {
            var length = Int();
            var text = Encoding.UTF8.GetString(data.Slice(position, length));
            position += length;
            return text;
        }
    }
}
