using System.Globalization;
using CommunityToolkit.Maui.Media;

namespace Clinic.App.Services;

/// <summary>
/// FR-AI-20: dictation into any text field with the platform speech engine (Android, iOS, macOS, Windows). Offline
/// recognition is used where the OS provides it, so it also works with no network. Nothing is recorded or sent
/// to a server: the platform returns text, and the person edits it like typed text.
/// </summary>
public sealed class DictationService
{
    private CancellationTokenSource? _cts;

    public static DictationService? Current { get; private set; }

    public DictationService() => Current = this;

    public bool IsListening { get; private set; }

    /// <summary>Listens until the speaker pauses; returns the recognised text, or null when cancelled or denied.</summary>
    public async Task<string?> ListenAsync(string language = "en")
    {
        if (IsListening) { await StopAsync(); return null; }
        var engine = SpeechToText.Default;
        _cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        if (!await engine.RequestPermissions(_cts.Token))
        {
            await Shell.Current.DisplayAlertAsync("Dictation", "Allow microphone and speech recognition to dictate.", "OK");
            return null;
        }

        var done = new TaskCompletionSource<string?>();
        void OnCompleted(object? s, SpeechToTextRecognitionResultCompletedEventArgs e) =>
            done.TrySetResult(e.RecognitionResult.IsSuccessful ? e.RecognitionResult.Text : null);
        engine.RecognitionResultCompleted += OnCompleted;
        IsListening = true;
        try
        {
            await engine.StartListenAsync(new SpeechToTextOptions
            {
                Culture = CultureInfo.GetCultureInfo(language == "ur" ? "ur-PK" : "en-US"),
                ShouldReportPartialResults = false,
                AutoStopSilenceTimeout = TimeSpan.FromSeconds(3),
            }, _cts.Token);
            using var reg = _cts.Token.Register(() => done.TrySetResult(null));
            return await done.Task;
        }
        catch (Exception ex) when (ex is FeatureNotSupportedException or InvalidOperationException or OperationCanceledException)
        {
            if (ex is not OperationCanceledException)
                await Shell.Current.DisplayAlertAsync("Dictation", $"Speech recognition is not available: {ex.Message}", "OK");
            return null;
        }
        finally
        {
            engine.RecognitionResultCompleted -= OnCompleted;
            IsListening = false;
            try { await engine.StopListenAsync(CancellationToken.None); } catch (Exception) { /* already stopped */ }
        }
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        try { await SpeechToText.Default.StopListenAsync(CancellationToken.None); } catch (Exception) { /* not listening */ }
    }
}

/// <summary>FR-AI-22: read instructions aloud for the patient in English or Urdu with the platform voices (offline).</summary>
public sealed class ReadAloudService
{
    private CancellationTokenSource? _cts;

    public async Task SpeakAsync(IEnumerable<string> lines, string language)
    {
        Stop();
        _cts = new CancellationTokenSource();
        var locales = await TextToSpeech.Default.GetLocalesAsync();
        var locale = locales.FirstOrDefault(l => l.Language.StartsWith(language, StringComparison.OrdinalIgnoreCase));
        if (locale is null && language == "ur")
            await Shell.Current.DisplayAlertAsync("Read aloud",
                "No Urdu voice is installed on this device. Add one in the system's text-to-speech settings; reading in the default voice.", "OK");
        var options = new SpeechOptions { Locale = locale, Rate = 0.9f };
        try
        {
            foreach (var line in lines)
            {
                _cts.Token.ThrowIfCancellationRequested();
                await TextToSpeech.Default.SpeakAsync(line, options, _cts.Token);
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Stop() => _cts?.Cancel();
}

/// <summary>FR-AI-21: the platform OCR engine (Windows.Media.Ocr, Apple Vision, Google ML Kit) turns a photo into text.</summary>
public interface ITextRecognizer
{
    bool IsSupported { get; }
    Task<string> RecognizeAsync(byte[] image, CancellationToken ct = default);
}

/// <summary>Takes or picks a photo of a document and returns its text, ready for <c>OcrExtraction</c>.</summary>
public sealed class DocumentScanner(ITextRecognizer recognizer)
{
    public bool IsSupported => recognizer.IsSupported;

    public async Task<string?> ScanAsync()
    {
        if (!recognizer.IsSupported)
        {
            await Shell.Current.DisplayAlertAsync("Scan", "Text recognition is not available on this device.", "OK");
            return null;
        }
        FileResult? photo;
        try
        {
            photo = MediaPicker.Default.IsCaptureSupported
                ? await MediaPicker.Default.CapturePhotoAsync()
                : await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "Photo of the document", FileTypes = FilePickerFileType.Images });
        }
        catch (Exception ex) when (ex is FeatureNotSupportedException or PermissionException)
        {
            await Shell.Current.DisplayAlertAsync("Scan", ex.Message, "OK");
            return null;
        }
        if (photo is null) return null;
        await using var stream = await photo.OpenReadAsync();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return await recognizer.RecognizeAsync(buffer.ToArray());
    }
}
