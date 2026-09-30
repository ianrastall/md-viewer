namespace MdViewer.Services;

public static class AppLog
{
    /// <summary>Where logs go; the test harness points this at a scratch folder.</summary>
    public static string Directory { get; set; } = ViewerSettings.DataDirectory;

    public static void Write(string category, Exception? exception, string fileName = "app.log")
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.AppendAllText(Path.Combine(Directory, fileName),
                $"{DateTimeOffset.Now:O} [{category}]{Environment.NewLine}{exception?.ToString() ?? "(no exception object)"}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Logging must never introduce a second failure.
        }
    }
}
