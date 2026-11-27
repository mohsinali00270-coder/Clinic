using Clinic.App.ViewModels;

namespace Clinic.App.Views;

public partial class ProcedurePage : ContentPage
{
    public ProcedurePage(ProcedureViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
        // FR-PRC-02: the signature pad's drawing becomes the PNG stored with the consent.
        vm.CaptureSignature = async () =>
        {
            if (SignaturePad.Lines.Count == 0) return null;
            await using var stream = await SignaturePad.GetImageStream(600, 200, CancellationToken.None);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            return buffer.ToArray();
        };
    }

    private void OnClearSignature(object? sender, EventArgs e) => SignaturePad.Clear();
}
