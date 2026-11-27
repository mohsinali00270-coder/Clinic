using Clinic.App.Services;
using Clinic.App.ViewModels;
using Clinic.App.Views;
using CommunityToolkit.Maui;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.LifecycleEvents;

namespace Clinic.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .UseMauiCommunityToolkit()
            .ConfigureLifecycleEvents(events =>
            {
                // FR-SEC-05: clicks and key presses count as activity for the auto-lock (taps on Android: MainActivity).
#if WINDOWS
                events.AddWindows(w => w.OnWindowCreated(window =>
                {
                    void Hook()
                    {
                        if (window.Content is not Microsoft.UI.Xaml.UIElement root) return;
                        root.AddHandler(Microsoft.UI.Xaml.UIElement.PointerPressedEvent,
                            new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => Touch()), handledEventsToo: true);
                        root.AddHandler(Microsoft.UI.Xaml.UIElement.KeyDownEvent,
                            new Microsoft.UI.Xaml.Input.KeyEventHandler((_, _) => Touch()), handledEventsToo: true);
                    }
                    var hooked = false;
                    window.Activated += (_, _) => { if (!hooked) { hooked = true; Hook(); } };
                }));
#elif IOS || MACCATALYST
                events.AddiOS(ios => ios.OnActivated(app =>
                {
                    foreach (var w in app.ConnectedScenes.OfType<UIKit.UIWindowScene>().SelectMany(s => s.Windows))
                    {
                        if (w.GestureRecognizers?.Any(g => g.Name == "clinic-activity") == true) continue;
                        w.AddGestureRecognizer(new UIKit.UITapGestureRecognizer(Touch) { CancelsTouchesInView = false, Name = "clinic-activity" });
                    }
                }));
#endif
            })
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
        s.AddSingleton<AppLock>();
        s.AddSingleton<ClientErrorReporter>();

        // On-device assist (§13.3): dictation, read-aloud, OCR — all offline, platform engines.
        s.AddSingleton<DictationService>();
        s.AddSingleton<ReadAloudService>();
        s.AddSingleton<ITextRecognizer, PlatformTextRecognizer>();
        s.AddSingleton<DocumentScanner>();

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
        s.AddTransient<CasePage>().AddTransient<CaseViewModel>();
        s.AddTransient<ReferralPage>().AddTransient<ReferralViewModel>();
        s.AddTransient<ProcedurePage>().AddTransient<ProcedureViewModel>();
        s.AddTransient<AdmissionPage>().AddTransient<AdmissionViewModel>();
        s.AddTransient<DayCarePage>().AddTransient<DayCareViewModel>();
        s.AddTransient<FollowUpsPage>().AddTransient<FollowUpsViewModel>();
        s.AddTransient<CareSetupPage>().AddTransient<CareSetupViewModel>();
        s.AddTransient<LabWorkPage>().AddTransient<LabWorkViewModel>();
        s.AddTransient<LabResultPage>().AddTransient<LabResultViewModel>();
        s.AddTransient<PatientBillingPage>().AddTransient<PatientBillingViewModel>();
        s.AddTransient<InvoicePage>().AddTransient<InvoiceViewModel>();
        s.AddTransient<CashClosingPage>().AddTransient<CashClosingViewModel>();
        s.AddTransient<ApprovalsPage>().AddTransient<ApprovalsViewModel>();
        s.AddTransient<ExpensesPage>().AddTransient<ExpensesViewModel>();
        s.AddTransient<ComplaintsPage>().AddTransient<ComplaintsViewModel>();
        s.AddTransient<StockPage>().AddTransient<StockViewModel>();
        s.AddTransient<DispensePage>().AddTransient<DispenseViewModel>();
        s.AddTransient<ReportsPage>().AddTransient<ReportsViewModel>();
        s.AddTransient<ConflictPage>().AddTransient<ConflictViewModel>();
        s.AddTransient<AuditPage>().AddTransient<AuditViewModel>();
        s.AddTransient<DuplicatesPage>().AddTransient<DuplicatesViewModel>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        var app = builder.Build();
        _ = app.Services.GetRequiredService<DictationService>(); // makes the dictation button available in DynamicFormView
        return app;
    }

    private static void Touch() => IPlatformApplication.Current?.Services.GetService<AppLock>()?.Touch();
}
