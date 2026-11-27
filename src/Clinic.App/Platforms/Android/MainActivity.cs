using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Clinic.App.Services;

namespace Clinic.App;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        // FR-SEC-06: no screenshots or screen recording, and a blank thumbnail in the recent-apps list.
        Window?.SetFlags(WindowManagerFlags.Secure, WindowManagerFlags.Secure);
    }

    /// <summary>FR-SEC-05: every touch counts as activity for the auto-lock.</summary>
    public override bool DispatchTouchEvent(MotionEvent? e)
    {
        if (e?.Action == MotionEventActions.Down)
            IPlatformApplication.Current?.Services.GetService<AppLock>()?.Touch();
        return base.DispatchTouchEvent(e);
    }
}
