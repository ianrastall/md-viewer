namespace MdViewer.Services;

public enum UnsavedChoice { Cancel, Save, Discard }

public interface IViewerDialogs
{
    Task<string?> PickOpenPathAsync();
    Task<string?> PickSavePathAsync(string suggestedName);
    Task<string?> PickExportPathAsync(string suggestedName);
    Task<string?> PromptUrlAsync();
    Task<UnsavedChoice> ConfirmUnsavedAsync(string documentName);
    Task ShowErrorAsync(string title, string message);
    void CloseWindow();
}
