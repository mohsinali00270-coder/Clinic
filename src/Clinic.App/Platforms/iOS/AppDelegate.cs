using Foundation;
using UIKit;

namespace Clinic.App;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
    private const nint BlurTag = 0x5EC6;

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    /// <summary>FR-SEC-06: blur the app before iOS takes the app-switcher snapshot.</summary>
    public override void OnResignActivation(UIApplication application)
    {
        base.OnResignActivation(application);
        foreach (var window in Windows())
        {
            if (window.ViewWithTag(BlurTag) is not null) continue;
            var blur = new UIVisualEffectView(UIBlurEffect.FromStyle(UIBlurEffectStyle.Regular))
            {
                Frame = window.Bounds,
                Tag = BlurTag,
                AutoresizingMask = UIViewAutoresizing.FlexibleWidth | UIViewAutoresizing.FlexibleHeight,
            };
            window.AddSubview(blur);
        }
    }

    public override void OnActivated(UIApplication application)
    {
        base.OnActivated(application);
        foreach (var window in Windows()) window.ViewWithTag(BlurTag)?.RemoveFromSuperview();
    }

    private static IEnumerable<UIWindow> Windows() =>
        UIApplication.SharedApplication.ConnectedScenes.OfType<UIWindowScene>().SelectMany(s => s.Windows);
}
