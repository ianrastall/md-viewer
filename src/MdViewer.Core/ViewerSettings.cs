using System.Text.Json;
using System.Text.Json.Serialization;

namespace MdViewer.Core;

public sealed class ViewerSettings
{
    public int ZoomPercent { get; set; } = 100;
    public double OutlineWidth { get; set; } = 280;
    public string? LastFolder { get; set; }
    [JsonIgnore] public string StoragePath { get; init; } = DefaultPath;

    /// <summary>
    /// The installed package's LocalState folder (a real path, removed with the app), or
    /// %LOCALAPPDATA%\md-viewer for development builds without package identity.
    /// </summary>
    public static string DataDirectory { get; } = ResolveDataDirectory();

    private static string ResolveDataDirectory()
    {
        try { return Windows.Storage.ApplicationData.Current.LocalFolder.Path; }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "md-viewer");
        }
    }
    public static string DefaultPath => Path.Combine(DataDirectory, "settings.json");

    public static ViewerSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            var settings = JsonSerializer.Deserialize<ViewerSettings>(File.ReadAllText(path)) ?? new();
            return new ViewerSettings { ZoomPercent = settings.ZoomPercent, OutlineWidth = settings.OutlineWidth, LastFolder = settings.LastFolder, StoragePath = path };
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new() { StoragePath = path }; }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StoragePath)!);
            File.WriteAllText(StoragePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
