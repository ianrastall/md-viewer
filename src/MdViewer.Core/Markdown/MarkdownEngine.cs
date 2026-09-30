using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace MdViewer.Core.Markdown;

/// <summary>Managed face of the C++ Markdown core (native/src/mdv_native.cpp).</summary>
public static unsafe partial class MarkdownEngine
{
    private const string Library = "mdv_native";
    private const int ExpectedAbi = 1;
    private static int abiChecked;

    [LibraryImport(Library, EntryPoint = "mdv_abi_version")]
    private static partial int AbiVersionNative();
    [LibraryImport(Library, EntryPoint = "mdv_parse")]
    private static partial byte* ParseNative(byte* utf8, nuint length, nuint* size);
    [LibraryImport(Library, EntryPoint = "mdv_reflow_headings")]
    private static partial byte* ReflowNative(byte* utf8, nuint length, nuint* size);
    [LibraryImport(Library, EntryPoint = "mdv_free")]
    private static partial void FreeNative(void* buffer);

    public static MarkdownDocument Parse(string markdown) => Call(() =>
    {
        var bytes = Encoding.UTF8.GetBytes(markdown);
        nuint size;
        byte* model;
        fixed (byte* input = bytes) model = ParseNative(input, (nuint)bytes.Length, &size);
        if (model == null) throw new NativeCoreException("The Markdown core ran out of memory.");
        try { return Read(new ReadOnlySpan<byte>(model, checked((int)size))); }
        finally { FreeNative(model); }
    });

    public static ReflowResult ReflowHeadings(string markdown) => Call(() =>
    {
        var bytes = Encoding.UTF8.GetBytes(markdown);
        nuint size;
        byte* output;
        fixed (byte* input = bytes) output = ReflowNative(input, (nuint)bytes.Length, &size);
        if (output == null) throw new NativeCoreException("The Markdown core ran out of memory.");
        string text;
        try { text = Encoding.UTF8.GetString(output, checked((int)size)); }
        finally { FreeNative(output); }
        if (!text.StartsWith("OK\n", StringComparison.Ordinal)) throw new NativeCoreException(text[(text.IndexOf('\n') + 1)..]);
        var headerEnd = text.IndexOf('\n', 3);
        var counts = text[3..headerEnd].Split('\t').Select(int.Parse).ToArray();
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

    private static T Call<T>(Func<T> action)
    {
        try
        {
            if (Volatile.Read(ref abiChecked) == 0)
            {
                var abi = AbiVersionNative();
                if (abi != ExpectedAbi) throw new NativeCoreException($"The C++ Markdown core has ABI {abi}; this build expects {ExpectedAbi}. Rebuild the application.");
                Volatile.Write(ref abiChecked, 1);
            }
            return action();
        }
        catch (DllNotFoundException) { throw new NativeCoreException("The C++ Markdown core (mdv_native.dll) is missing. Run scripts\\build.ps1 to build it."); }
        catch (BadImageFormatException) { throw new NativeCoreException("The C++ Markdown core must match the application's x64 architecture."); }
        catch (EntryPointNotFoundException) { throw new NativeCoreException("The C++ Markdown core has an incompatible ABI. Rebuild the application."); }
    }

    private static MarkdownDocument Read(ReadOnlySpan<byte> data)
    {
        var reader = new ModelReader(data);
        var magic = Encoding.ASCII.GetString(data[..4]);
        reader.Skip(4);
        if (magic == "MDVE") throw new NativeCoreException(reader.String());
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
