namespace MdViewer.Core;

public static class AppLog
{
    public static void Write(string category, Exception? exception, string fileName = "app.log")
    {
        try
        {
            Directory.CreateDirectory(ViewerSettings.DataDirectory);
            File.AppendAllText(Path.Combine(ViewerSettings.DataDirectory, fileName),
                $"{DateTimeOffset.Now:O} [{category}]{Environment.NewLine}{exception?.ToString() ?? "(no exception object)"}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Logging must never introduce a second failure.
        }
    }
}
