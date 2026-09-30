using MdViewer.Core;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;

namespace MdViewer;

public partial class App : Application
{
    private MainWindow? window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => AppLog.Write("Unhandled XAML exception", e.Exception, "crash.log");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => AppLog.Write("Unhandled exception", e.ExceptionObject as Exception, "crash.log");
        TaskScheduler.UnobservedTaskException += (_, e) => AppLog.Write("Unobserved task exception", e.Exception, "crash.log");
    }

    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        window = new MainWindow();
        window.Activate();
        await window.StartAsync(LaunchFile());
    }

    /// <summary>File Explorer's "Open with" arrives as file activation (packaged) or as a command-line path.</summary>
    private static string? LaunchFile()
    {
        try
        {
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            if (activation.Kind == ExtendedActivationKind.File && activation.Data is IFileActivatedEventArgs file && file.Files.Count > 0)
                return file.Files[0].Path;
        }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // Development runs without package identity have no activation details.
        }
        return Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(File.Exists);
    }
}
