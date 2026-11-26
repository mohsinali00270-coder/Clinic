using Clinic.App.Services;
using Clinic.App.ViewModels;
using Clinic.App.Views;
using CommunityToolkit.Maui;
using Microsoft.Extensions.Logging;

namespace Clinic.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .UseMauiCommunityToolkit()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

        var s = builder.Services;

        // Core services
        s.AddSingleton<AppSettings>();
        s.AddSingleton<DeviceIdentity>();
        s.AddSingleton<SessionStore>();
        s.AddSingleton<ApiClient>();
        s.AddSingleton<AuthService>();
        s.AddSingleton<AdminApi>();
        s.AddSingleton<SyncCoordinator>();
        s.AddSingleton<OfflinePinService>();
        s.AddSingleton<FormDesignerSession>();

        // Shell
        s.AddSingleton<AppShell>();

        // Pages + view models
        s.AddTransient<LoginPage>().AddTransient<LoginViewModel>();
        s.AddTransient<HomePage>().AddTransient<HomeViewModel>();
        s.AddTransient<BranchesPage>().AddTransient<BranchesViewModel>();
        s.AddTransient<BranchEditPage>().AddTransient<BranchEditViewModel>();
        s.AddTransient<UsersPage>().AddTransient<UsersViewModel>();
        s.AddTransient<UserEditPage>().AddTransient<UserEditViewModel>();
        s.AddTransient<DevicesPage>().AddTransient<DevicesViewModel>();
        s.AddTransient<UnlockPage>().AddTransient<UnlockViewModel>();
        s.AddTransient<NotesPage>().AddTransient<NotesViewModel>();
        s.AddTransient<NoteEditPage>().AddTransient<NoteEditViewModel>();
        s.AddTransient<SyncCenterPage>().AddTransient<SyncCenterViewModel>();
        s.AddTransient<FormsPage>().AddTransient<FormsViewModel>();
        s.AddTransient<FormDesignerPage>().AddTransient<FormDesignerViewModel>();
        s.AddTransient<FieldEditorPage>().AddTransient<FieldEditorViewModel>();
        s.AddTransient<FormPreviewPage>().AddTransient<FormPreviewViewModel>();
        s.AddTransient<LookupsPage>().AddTransient<LookupsViewModel>();
        s.AddTransient<LookupEditPage>().AddTransient<LookupEditViewModel>();
        s.AddTransient<PatientsPage>().AddTransient<PatientsViewModel>();
        s.AddTransient<PatientEditPage>().AddTransient<PatientEditViewModel>();
        s.AddTransient<PatientPage>().AddTransient<PatientViewModel>();
        s.AddTransient<BookingPage>().AddTransient<BookingViewModel>();
        s.AddTransient<AppointmentsPage>().AddTransient<AppointmentsViewModel>();
        s.AddTransient<QueuePage>().AddTransient<QueueViewModel>();
        s.AddTransient<DoctorsPage>().AddTransient<DoctorsViewModel>();
        s.AddTransient<DoctorEditPage>().AddTransient<DoctorEditViewModel>();
        s.AddTransient<SchedulingSetupPage>().AddTransient<SchedulingSetupViewModel>();
        s.AddTransient<EncounterPage>().AddTransient<EncounterViewModel>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
