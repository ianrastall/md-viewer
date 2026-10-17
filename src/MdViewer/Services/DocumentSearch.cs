using MdViewer.Interop.Markdown;

namespace MdViewer.Services;

public sealed record SearchMatch(int SegmentIndex, int BlockIndex, int TextIndex, int Start, int Length);
public sealed record SearchPresentation(string Query, bool MatchCase, SearchMatch? Match);
internal sealed record SearchSegment(int BlockIndex, int TextIndex, string Text);

/// <summary>Bounded counting and constant-space navigation, including overlapping matches.</summary>
internal sealed class DocumentSearch(IReadOnlyList<SearchSegment> segments)
{
    public const int CountLimit = 10000;
    private string query = "";
    private StringComparison comparison;
    public int Count { get; private set; }
    public bool CountLimited { get; private set; }
    public int Ordinal { get; private set; }
    public SearchMatch? Match { get; private set; }

    public static DocumentSearch Source(string text) => new([new(-1, 0, text)]);

    public static DocumentSearch Preview(MarkdownDocument? document)
    {
        var segments = new List<SearchSegment>();
        if (document is not null)
            for (var block = 0; block < document.Blocks.Count; block++)
            {
                var index = 0;
                foreach (var text in MarkdownDisplay.Segments(document.Blocks[block])) segments.Add(new(block, index++, text));
            }
        return new(segments);
    }

    public void SetQuery(string value, bool matchCase, SearchMatch? previous = null)
    {
        query = value;
        comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        Count = Ordinal = 0;
        CountLimited = false;
        Match = null;
        if (query.Length == 0) return;
        for (var segment = 0; segment < segments.Count && !CountLimited; segment++)
        {
            var text = segments[segment].Text;
            for (var start = text.IndexOf(query, comparison); start >= 0; start = text.IndexOf(query, start + 1, comparison))
            {
                Count++;
                Match ??= At(segment, start);
                if (Count > CountLimit) { Count = CountLimit; CountLimited = true; break; }
            }
        }
        if (Match is null) return;
        Ordinal = 1;
        if (previous is not null)
        {
            var segment = Math.Clamp(previous.SegmentIndex, 0, segments.Count - 1);
            var start = Math.Clamp(previous.Start, 0, segments[segment].Text.Length);
            var found = segments[segment].Text.IndexOf(query, start, comparison);
            if (found >= 0) Match = At(segment, found);
            if (!CountLimited) Ordinal = Rank(Match);
        }
    }

    public SearchMatch? Move(bool backwards)
    {
        if (Match is not { } current) return null;
        var segment = current.SegmentIndex;
        var text = segments[segment].Text;
        var found = backwards
            ? current.Start == 0 ? -1 : text.LastIndexOf(query, current.Start + query.Length - 2, comparison)
            : text.IndexOf(query, current.Start + 1, comparison);
        for (var visited = 0; found < 0 && visited < segments.Count; visited++)
        {
            segment = (segment + (backwards ? segments.Count - 1 : 1)) % segments.Count;
            found = backwards ? segments[segment].Text.LastIndexOf(query, comparison) : segments[segment].Text.IndexOf(query, comparison);
        }
        if (found < 0) return Match;
        Match = At(segment, found);
        if (!CountLimited) Ordinal = (Ordinal - 1 + (backwards ? Count - 1 : 1)) % Count + 1;
        return Match;
    }

    private SearchMatch At(int segment, int start) => new(segment, segments[segment].BlockIndex, segments[segment].TextIndex, start, query.Length);

    private int Rank(SearchMatch match)
    {
        var rank = 0;
        for (var segment = 0; segment <= match.SegmentIndex; segment++)
        {
            var text = segments[segment].Text;
            for (var start = text.IndexOf(query, comparison); start >= 0; start = text.IndexOf(query, start + 1, comparison))
            {
                rank++;
                if (segment == match.SegmentIndex && start == match.Start) return rank;
            }
        }
        return 1;
    }
}
