using System.Windows;
using Artemis.App.ViewModels;
using Artemis.App.Views;
using Artemis.Core.Logging;
using Artemis.Core.Playsets;

namespace Artemis.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Error("Unhandled error", args.Exception);
            MessageBox.Show(args.Exception.Message, "Artemis", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        // --data-dir keeps a run away from the real app data (tests, experiments).
        var i = Array.IndexOf(e.Args, "--data-dir");
        var paths = i >= 0 && i + 1 < e.Args.Length ? new AppPaths(e.Args[i + 1]) : AppPaths.Default();
        AppLog.Start(paths.LogFile);
        AppDomain.CurrentDomain.UnhandledException += (_, args) => AppLog.Error("Unhandled error", args.ExceptionObject as Exception);

        // --user-dir does the same for the game's Documents folder (mods, dlc_load.json), so tests never touch the real one.
        var u = Array.IndexOf(e.Args, "--user-dir");
        var userDir = u >= 0 && u + 1 < e.Args.Length ? e.Args[u + 1] : null;

        // --orion-dir points at the folder holding the winmm.dll stub and orion.dll to install (tests); normally the "orion" folder next to Artemis.exe.
        var o = Array.IndexOf(e.Args, "--orion-dir");
        var orionDir = o >= 0 && o + 1 < e.Args.Length ? e.Args[o + 1] : null;

        new MainWindow(new MainViewModel(paths, new Dialogs(), userDir, orionDir)).Show();
    }
}
