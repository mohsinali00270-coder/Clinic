namespace Clinic.App.Services;

/// <summary>
/// FR-AI-21 on each platform, all on the device (no network): Windows.Media.Ocr on Windows, the Vision framework on
/// iOS / macOS, Google ML Kit (bundled Latin model) on Android.
/// </summary>
public sealed class PlatformTextRecognizer : ITextRecognizer
{
#if WINDOWS
    public bool IsSupported => Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages() is not null;

    public async Task<string> RecognizeAsync(byte[] image, CancellationToken ct = default)
    {
        var engine = Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages()
                     ?? throw new InvalidOperationException("No OCR language is installed in Windows.");
        using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        using (var writer = new Windows.Storage.Streams.DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(image);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        stream.Seek(0);
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
        using var bitmap = await decoder.GetSoftwareBitmapAsync();
        var result = await engine.RecognizeAsync(bitmap);
        return string.Join("\n", result.Lines.Select(l => l.Text));
    }
#elif IOS || MACCATALYST
    public bool IsSupported => true;

    public Task<string> RecognizeAsync(byte[] image, CancellationToken ct = default)
    {
        var done = new TaskCompletionSource<string>();
        using var data = Foundation.NSData.FromArray(image);
        using var ui = UIKit.UIImage.LoadFromData(data) ?? throw new InvalidOperationException("The photo could not be read.");
        var request = new Vision.VNRecognizeTextRequest((req, error) =>
        {
            if (error is not null) { done.TrySetException(new InvalidOperationException(error.LocalizedDescription)); return; }
            var lines = req.GetResults<Vision.VNRecognizedTextObservation>()?
                .Select(o => o.TopCandidates(1).FirstOrDefault()?.String)
                .Where(s => !string.IsNullOrEmpty(s)) ?? [];
            done.TrySetResult(string.Join("\n", lines));
        })
        {
            RecognitionLevel = Vision.VNRequestTextRecognitionLevel.Accurate,
            UsesLanguageCorrection = true,
        };
        var handler = new Vision.VNImageRequestHandler(ui.CGImage!, new Vision.VNImageOptions());
        Task.Run(() =>
        {
            handler.Perform([request], out var error);
            if (error is not null) done.TrySetException(new InvalidOperationException(error.LocalizedDescription));
        }, ct);
        return done.Task;
    }
#elif ANDROID
    public bool IsSupported => true;

    public async Task<string> RecognizeAsync(byte[] image, CancellationToken ct = default)
    {
        using var bitmap = await Android.Graphics.BitmapFactory.DecodeByteArrayAsync(image, 0, image.Length)
                           ?? throw new InvalidOperationException("The photo could not be read.");
        using var input = Xamarin.Google.MLKit.Vision.Common.InputImage.FromBitmap(bitmap, 0);
        using var recognizer = Xamarin.Google.MLKit.Vision.Text.TextRecognition.GetClient(
            Xamarin.Google.MLKit.Vision.Text.Latin.TextRecognizerOptions.DefaultOptions);
        var done = new TaskCompletionSource<string>();
        recognizer.Process(input)
            .AddOnSuccessListener(new Listener(r => done.TrySetResult(((Xamarin.Google.MLKit.Vision.Text.Text)r!).GetText())))
            .AddOnFailureListener(new FailureListener(e => done.TrySetException(e)));
        using var reg = ct.Register(() => done.TrySetCanceled());
        return await done.Task;
    }

    private sealed class Listener(Action<Java.Lang.Object?> onSuccess) : Java.Lang.Object, Android.Gms.Tasks.IOnSuccessListener
    {
        public void OnSuccess(Java.Lang.Object? result) => onSuccess(result);
    }

    private sealed class FailureListener(Action<Exception> onFailure) : Java.Lang.Object, Android.Gms.Tasks.IOnFailureListener
    {
        public void OnFailure(Java.Lang.Exception e) => onFailure(e);
    }
#else
    public bool IsSupported => false;
    public Task<string> RecognizeAsync(byte[] image, CancellationToken ct = default) => throw new PlatformNotSupportedException();
#endif
}
