namespace MdViewer.ViewModels;

/// <summary>Shared source/Format/Reflow snapshots, bounded across both directions.</summary>
internal sealed class DocumentHistory
{
    private readonly int maxSteps;
    private readonly long maxCharacters;
    private readonly List<string> undo = [], redo = [];

    public DocumentHistory(int maxSteps = 100, long maxCharacters = 16 * 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSteps, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCharacters, 1);
        this.maxSteps = maxSteps;
        this.maxCharacters = maxCharacters;
    }

    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public int Count => undo.Count + redo.Count;
    public long CharacterCount { get; private set; }

    public void Record(string text)
    {
        ClearRedo();
        Push(undo, text);
        Trim(undo, redo);
    }

    public string Undo(string current) => Restore(undo, redo, current);
    public string Redo(string current) => Restore(redo, undo, current);

    public void Clear()
    {
        undo.Clear();
        redo.Clear();
        CharacterCount = 0;
    }

    public void ClearRedo()
    {
        foreach (var text in redo) CharacterCount -= text.Length;
        redo.Clear();
    }

    private string Restore(List<string> source, List<string> target, string current)
    {
        if (source.Count == 0) throw new InvalidOperationException("There is no history to restore.");
        var text = source[^1];
        source.RemoveAt(source.Count - 1);
        CharacterCount -= text.Length;
        Push(target, current);
        // Keep the just-created reverse step; evict the farthest opposite history first.
        Trim(source, target);
        return text;
    }

    private void Push(List<string> target, string text)
    {
        target.Add(text);
        CharacterCount += text.Length;
    }

    private void Trim(List<string> first, List<string> second)
    {
        // A single oversized snapshot remains reversible. Further snapshots evict
        // distant steps rather than silently disabling Undo for a large document.
        while (Count > 1 && (Count > maxSteps || CharacterCount > maxCharacters))
        {
            var oldest = first.Count > 0 ? first : second;
            CharacterCount -= oldest[0].Length;
            oldest.RemoveAt(0);
        }
    }
}
