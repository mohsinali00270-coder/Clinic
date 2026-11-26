using Android.App;
using Android.Runtime;

namespace Clinic.App;

// FR-SEC-10: the encrypted local database is never included in Google cloud backups.
#if DEBUG
// Development only: allows http://10.0.2.2 to reach the local API. Release builds require HTTPS (FR-SEC-01).
[Application(UsesCleartextTraffic = true, AllowBackup = false)]
#else
[Application(AllowBackup = false)]
#endif
public class MainApplication : MauiApplication
{
	public MainApplication(IntPtr handle, JniHandleOwnership ownership)
		: base(handle, ownership)
	{
	}

	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
