using System.Windows;

namespace StudyBlocker.App;

public partial class App : Application
{
    // Run with this switch, the app only makes the open browsers re-read the
    // service's policies (BrowserRefresh) and exits, with no window. LockIn
    // runs it after every change it makes (a session starting, a break, the
    // session ending): browsers otherwise take up to 15 minutes to notice,
    // and this window, which pushes them itself, usually isn't open then.
    public const string RefreshBrowsersSwitch = "--refresh-browsers";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Opened in a blocked program's place: the notice, and nothing else.
        if (e.Args.Contains(BlockedAppNotice.Switch))
        {
            BlockedAppNotice.Show(e.Args);
            Shutdown();
            return;
        }
        if (e.Args.Contains(RefreshBrowsersSwitch))
        {
            BrowserRefresh.RefreshRunning();
            Shutdown();
            return;
        }
        new MainWindow().Show();
    }
}
