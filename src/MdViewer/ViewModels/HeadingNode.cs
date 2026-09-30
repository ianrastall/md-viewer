using System.Collections.ObjectModel;
using MdViewer.Core.Markdown;

namespace MdViewer.ViewModels;

public sealed class HeadingNode(HeadingInfo heading)
{
    public HeadingInfo Heading { get; } = heading;
    public string Title => Heading.Title;
    public ObservableCollection<HeadingNode> Children { get; } = [];
    public override string ToString() => Title;

    /// <summary>Nests headings under the nearest preceding heading of a lower level.</summary>
    public static IReadOnlyList<HeadingNode> BuildTree(IEnumerable<HeadingInfo> headings)
    {
        var roots = new List<HeadingNode>();
        var stack = new Stack<HeadingNode>();
        foreach (var heading in headings)
        {
            var node = new HeadingNode(heading);
            while (stack.Count > 0 && stack.Peek().Heading.Level >= heading.Level) stack.Pop();
            if (stack.Count == 0) roots.Add(node); else stack.Peek().Children.Add(node);
            stack.Push(node);
        }
        return roots;
    }
}
