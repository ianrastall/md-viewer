using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MdViewer.Interop;
using MdViewer.Services;

namespace MdViewer.ViewModels;

/// <summary>One program md-viewer depends on, as the Tools window shows it.</summary>
public sealed partial class ToolCard(string id) : ObservableObject
{
    public string Id { get; } = id;
    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string Purpose { get; set; } = "";
    [ObservableProperty] public partial string StatusText { get; set; } = "Checking...";
    [ObservableProperty] public partial string Location { get; set; } = "";
    [ObservableProperty] public partial string LatestText { get; set; } = "";
    [ObservableProperty] public partial string ActionLabel { get; set; } = "";
    [ObservableProperty] public partial string ActionNote { get; set; } = "";
    [ObservableProperty] public partial string LanguagesText { get; set; } = "";
    [ObservableProperty] public partial bool Installed { get; set; }
    [ObservableProperty] public partial bool NeedsAttention { get; set; }
    [ObservableProperty] public partial bool HasAction { get; set; }
    public bool IsCurrent => !NeedsAttention;
    partial void OnNeedsAttentionChanged(bool value) => OnPropertyChanged(nameof(IsCurrent));

    /// <summary>Shows a status; a refresh without an update check keeps the previous check's result for an unchanged install.</summary>
    internal void Apply(ToolStatus status, ToolStatus? previous)
    {
        Name = status.Name;
        Purpose = status.Purpose;
        Installed = status.Installed;
        StatusText = status.Installed
            ? $"Installed{(status.Version is { } v ? " " + v : "")}{status.Source switch { "md-viewer" => " · md-viewer's copy", "system" => " · system copy", _ => "" }}"
            : status.Id == "windows-ocr" ? "No OCR languages installed" : "Not installed";
        Location = status.Path ?? status.Note ?? "";
        var checkedNow = status.Latest is not null || status.LatestError is not null;
        var reuse = !checkedNow && previous is not null && previous.Version == status.Version && previous.Path == status.Path;
        var latest = checkedNow ? status.Latest : reuse ? previous!.Latest : null;
        var error = checkedNow ? status.LatestError : reuse ? previous!.LatestError : null;
        var updateAvailable = checkedNow ? status.UpdateAvailable : reuse && previous!.UpdateAvailable;
        LatestText = status.Action == "none" ? ""
            : error is not null ? $"Could not check for updates: {error}"
            : latest is null ? "Not checked for updates yet."
            : updateAvailable ? $"Version {latest} is available."
            : status.Installed ? $"Up to date (the latest release is {latest})."
            : $"The latest release is {latest}.";
        NeedsAttention = status.Action != "none" && (!status.Installed || updateAvailable);
        HasAction = status.Action != "none";
        ActionLabel = status.Id == "pandoc" ? (status.Installed ? "Download latest" : "Download") : status.Installed ? "Run latest installer" : "Install";
        ActionNote = status.ActionNote ?? "";
        LanguagesText = status.Languages.Length switch
        {
            0 => "",
            <= 12 => "Languages: " + string.Join(", ", status.Languages),
            var n => $"{n} languages installed, including {string.Join(", ", status.Languages.Where(l => l is "eng" or "deu" or "fra" or "spa" or "ita" or "rus").DefaultIfEmpty(status.Languages[0]))}"
        };
    }
}

/// <summary>The Tools window: Pandoc and Tesseract status, updates, installs, and OCR preferences.</summary>
public sealed partial class ToolsViewModel : ObservableObject
{
    private static readonly string[] Engines = ["auto", "windows", "tesseract"];
    private readonly ViewerSettings settings;
    private CancellationTokenSource? operation;
    private ToolsStatus? lastLatest;

    public ToolsViewModel(ViewerSettings settings)
    {
        this.settings = settings;
        EngineIndex = Math.Max(0, Array.IndexOf(Engines, settings.OcrEngine));
        TesseractLanguages = settings.TesseractLanguages;
        CheckAutomatically = settings.CheckToolsAutomatically;
        LastChecked = Describe(settings.LastToolCheck);
    }

    public ToolCard Pandoc { get; } = new("pandoc");
    public ToolCard Tesseract { get; } = new("tesseract");
    public ToolCard WindowsOcr { get; } = new("windows-ocr");
    [ObservableProperty] public partial bool IsWorking { get; private set; }
    [ObservableProperty] public partial string Status { get; private set; } = "";
    [ObservableProperty] public partial string LastChecked { get; private set; } = "";
    [ObservableProperty] public partial string ActiveEngineText { get; private set; } = "";
    [ObservableProperty] public partial int EngineIndex { get; set; }
    [ObservableProperty] public partial string TesseractLanguages { get; set; } = "eng";
    [ObservableProperty] public partial bool CheckAutomatically { get; set; }
    public bool IsIdle => !IsWorking;

    partial void OnIsWorkingChanged(bool value) => OnPropertyChanged(nameof(IsIdle));
    partial void OnEngineIndexChanged(int value) => SaveOcr();
    partial void OnTesseractLanguagesChanged(string value) => SaveOcr();
    partial void OnCheckAutomaticallyChanged(bool value)
    {
        settings.CheckToolsAutomatically = value;
        settings.Save();
    }

    private void SaveOcr()
    {
        settings.OcrEngine = Engines[Math.Clamp(EngineIndex, 0, Engines.Length - 1)];
        settings.TesseractLanguages = string.IsNullOrWhiteSpace(TesseractLanguages) ? "eng" : TesseractLanguages.Trim().Replace(' ', '+').Replace(',', '+');
        settings.Save();
        try { Core.ConfigureOcr(settings.OcrEngine, settings.TesseractLanguages); }
        catch (NativeCoreException ex) { Status = ex.Message; }
        UpdateActiveEngine();
    }

    private void UpdateActiveEngine()
    {
        var tesseract = Tesseract.Installed && settings.OcrEngine != "windows";
        ActiveEngineText = tesseract
            ? $"Scanned pages are read with Tesseract ({settings.TesseractLanguages})."
            : settings.OcrEngine == "tesseract" ? "Tesseract is not installed, so scanned pages are read with Windows OCR."
            : "Scanned pages are read with Windows OCR.";
    }

    /// <summary>Reads what is installed; with <paramref name="checkLatest"/>, also asks GitHub for the newest releases.</summary>
    public async Task RefreshAsync(bool checkLatest)
    {
        if (IsWorking) return;
        await RunAsync(checkLatest ? "Checking for updates..." : "Checking installed tools...", async token =>
        {
            var status = await Core.ToolsStatusAsync(checkLatest, new Progress<string>(s => Status = s), token);
            if (status.CheckedLatest)
            {
                lastLatest = status;
                settings.LastToolCheck = DateTimeOffset.Now;
                settings.Save();
                LastChecked = Describe(settings.LastToolCheck);
            }
            Apply(status);
            Status = checkLatest ? UpdateSummary(status) : "";
        });
    }

    [RelayCommand]
    private Task CheckForUpdatesAsync() => RefreshAsync(checkLatest: true);

    [RelayCommand]
    private async Task InstallAsync(string tool)
    {
        if (IsWorking) return;
        string? message = null;
        await RunAsync($"Preparing {tool}...", async token => message = await Core.InstallToolAsync(tool, new Progress<string>(s => Status = s), token));
        if (message is null) return;
        await RefreshAsync(checkLatest: false);
        Status = message;
    }

    [RelayCommand]
    private void Cancel()
    {
        operation?.Cancel();
        Status = "Stopping...";
    }

    private void Apply(ToolsStatus status)
    {
        foreach (var card in new[] { Pandoc, Tesseract, WindowsOcr })
            if (status[card.Id] is { } tool) card.Apply(tool, lastLatest?[card.Id]);
        UpdateActiveEngine();
    }

    private async Task RunAsync(string start, Func<CancellationToken, Task> work)
    {
        using var cancellation = new CancellationTokenSource();
        operation = cancellation;
        IsWorking = true;
        Status = start;
        try { await work(cancellation.Token); }
        catch (OperationCanceledException) { Status = "Stopped."; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Write("Tools", ex);
            Status = ex.Message;
        }
        finally { IsWorking = false; operation = null; }
    }

    internal static string UpdateSummary(ToolsStatus status)
    {
        var updates = status.Tools.Where(t => t.UpdateAvailable).Select(t => $"{t.Name} {t.Latest}").ToArray();
        var errors = status.Tools.Count(t => t.LatestError is not null);
        return updates.Length > 0 ? $"Updates available: {string.Join(", ", updates)}."
            : errors > 0 ? "Some update checks failed; see below."
            : "Everything is up to date.";
    }

    private static string Describe(DateTimeOffset? checkedAt) => checkedAt is { } at ? $"Last checked {at.LocalDateTime:g}." : "Not checked for updates yet.";
}
