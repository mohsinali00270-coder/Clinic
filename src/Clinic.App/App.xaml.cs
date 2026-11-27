using Clinic.App.Services;

namespace Clinic.App;

public partial class App : Application
{
    private readonly IServiceProvider _services;

    public App(IServiceProvider services)
    {
        InitializeComponent();
        _services = services;
        AppSettings.ApplyTheme(services.GetRequiredService<AppSettings>().Theme);

        // Crash reports (§11): written to disk at once, sent to the server after the next sign-in or sync.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) ClientErrorReporter.Record("crash", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            ClientErrorReporter.Record("unobserved", e.Exception);
            e.SetObserved();
        };
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(_services.GetRequiredService<AppShell>()) { Title = "Clinic" };
        var appLock = _services.GetRequiredService<AppLock>();
        // FR-SEC-05: time spent in the background counts towards the auto-lock.
        window.Stopped += (_, _) => appLock.OnBackground();
        window.Deactivated += (_, _) => appLock.OnBackground();
        window.Resumed += (_, _) => appLock.OnForeground();
        window.Activated += (_, _) => appLock.OnForeground();
        return window;
    }
}
