using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MdViewer.Services;

namespace MdViewer.ViewModels;

public partial class MainViewModel
{
    private DocumentSearch? find;
    private bool findSourceDirty;

    [ObservableProperty] public partial bool IsFindOpen { get; private set; }
    [ObservableProperty] public partial string FindQuery { get; set; } = "";
    [ObservableProperty] public partial bool FindMatchCase { get; set; }

    public string FindPlaceholder => IsRawView ? "Find in Markdown source" : "Find in rendered text";
    public string FindSummary => FindQuery.Length == 0 ? IsRawView ? "Markdown source" : "Rendered text"
        : find is not { Count: > 0 } ? "No matches"
        : find.CountLimited ? $"{DocumentSearch.CountLimit:N0}+ matches" : $"{find.Ordinal:N0} of {find.Count:N0}";
    public bool CanFindNavigate => IsFindOpen && !IsBusy && find is { Count: > 0 };
    public SearchMatch? FindMatch => find?.Match;
    public SearchPresentation? FindPresentation => IsFindOpen && IsRichView && FindQuery.Length > 0 ? new(FindQuery, FindMatchCase, FindMatch) : null;

    /// <summary>True requests navigation; false updates counts/highlights without moving the source caret.</summary>
    public event EventHandler<bool>? FindChanged;
    public event EventHandler? FindFocusRequested;

    [RelayCommand]
    private void Find()
    {
        if (IsBusy) return;
        IsFindOpen = true;
        FindFocusRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand] private void CloseFind() => IsFindOpen = false;
    [RelayCommand(CanExecute = nameof(CanFindNavigate))] private void FindNext() => MoveFind(false);
    [RelayCommand(CanExecute = nameof(CanFindNavigate))] private void FindPrevious() => MoveFind(true);

    partial void OnIsFindOpenChanged(bool value)
    {
        if (value) RefreshFind(rebuild: true, navigate: true);
        else { find = null; NotifyFind(navigate: false); }
    }

    partial void OnFindQueryChanged(string value) => RefreshFind(rebuild: false, navigate: true);
    partial void OnFindMatchCaseChanged(bool value) => RefreshFind(rebuild: false, navigate: true);

    private void MoveFind(bool backwards)
    {
        if (IsBusy || !IsFindOpen) return;
        if (IsRawView && findSourceDirty) RefreshFind(rebuild: true, navigate: false);
        if (find?.Move(backwards) is null) return;
        NotifyFind(navigate: true);
    }

    private void RefreshFind(bool rebuild, bool navigate)
    {
        if (!IsFindOpen) return;
        var previous = rebuild && !navigate ? find?.Match : null;
        if (find is null || rebuild || IsRawView && findSourceDirty)
        {
            find = IsRawView ? DocumentSearch.Source(SourceText) : DocumentSearch.Preview(Document);
            findSourceDirty = false;
        }
        find.SetQuery(FindQuery, FindMatchCase, previous);
        NotifyFind(navigate);
    }

    private void NotifyFind(bool navigate, bool notifyView = true)
    {
        OnPropertyChanged(nameof(FindSummary));
        OnPropertyChanged(nameof(CanFindNavigate));
        FindNextCommand.NotifyCanExecuteChanged();
        FindPreviousCommand.NotifyCanExecuteChanged();
        if (notifyView) FindChanged?.Invoke(this, navigate);
    }
}
