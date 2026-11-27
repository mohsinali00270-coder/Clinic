using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace Clinic.Client.Core.Clinical;

/// <summary>A titled block of a letter. Urdu blocks are shaped right-to-left.</summary>
public sealed record LetterSection(string Title, IReadOnlyList<string> Lines, bool Urdu = false);

/// <summary>A one-off clinical document: referral letter (FR-REF-02), discharge summary (FR-PRC-03), consent (FR-PRC-02).</summary>
public sealed record ClinicLetter(
    string Title,
    string ClinicName,
    string BranchLine,
    string DoctorName,
    string DoctorDetails,
    string PatientName,
    string PatientDetails,
    DateTime Date,
    IReadOnlyList<LetterSection> Sections,
    string? SignatureLabel = null,
    byte[]? SignaturePng = null,
    string? SignatureCaption = null);

/// <summary>Renders <see cref="ClinicLetter"/> as A4 PDF on the device (offline), in the prescription's house style.</summary>
public static class ClinicLetterPdf
{
    private const float PageWidth = 595;
    private const float PageHeight = 842;
    private const float Margin = 48;
    private const float ContentWidth = PageWidth - 2 * Margin;

    public static byte[] Render(ClinicLetter letter)
    {
        using var stream = new MemoryStream();
        using (var pdf = SKDocument.CreatePdf(stream, new SKDocumentPdfMetadata
               {
                   Title = $"{letter.Title} – {letter.PatientName}", Author = letter.DoctorName, Creator = "Clinic", Creation = DateTime.Now,
               }))
        {
            using var writer = new Writer(pdf, letter);
            writer.Run();
            pdf.Close();
        }
        return stream.ToArray();
    }

    private sealed class Writer(SKDocument pdf, ClinicLetter doc) : IDisposable
    {
        private SKBitmap? _keepAlive;
        private SKImage? _keepAliveImage;

        public void Dispose()
        {
            _keepAliveImage?.Dispose();
            _keepAlive?.Dispose();
        }

        private readonly SKTypeface _regular = SKTypeface.FromFamilyName("Arial") ?? SKTypeface.Default;
        private readonly SKTypeface _bold = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold) ?? SKTypeface.Default;
        private readonly SKTypeface? _urdu = SKFontManager.Default.MatchCharacter('ا');
        private readonly SKPaint _ink = new() { Color = new SKColor(0x22, 0x22, 0x22), IsAntialias = true };
        private readonly SKPaint _muted = new() { Color = new SKColor(0x6B, 0x72, 0x80), IsAntialias = true };
        private readonly SKPaint _accent = new() { Color = new SKColor(0x0F, 0x76, 0x6E), IsAntialias = true };
        private readonly SKPaint _rule = new() { Color = new SKColor(0xD1, 0xD5, 0xDB), StrokeWidth = 1, IsAntialias = true };
        private SKCanvas _canvas = null!;
        private float _y;
        private int _page;

        public void Run()
        {
            NewPage();
            Header();
            foreach (var s in doc.Sections) Section(s);
            Signature();
            Footer();
            pdf.EndPage();
        }

        private void NewPage()
        {
            if (_page > 0) { Footer(); pdf.EndPage(); }
            _canvas = pdf.BeginPage(PageWidth, PageHeight);
            _page++;
            _y = Margin;
        }

        private void Ensure(float h)
        {
            if (_y + h > PageHeight - Margin - 30) NewPage();
        }

        private void Header()
        {
            using var clinic = new SKFont(_bold, 18);
            using var small = new SKFont(_regular, 9);
            using var bold = new SKFont(_bold, 12);
            using var title = new SKFont(_bold, 14);
            _canvas.DrawText(doc.ClinicName, Margin, _y + 16, SKTextAlign.Left, clinic, _accent);
            _canvas.DrawText(doc.DoctorName, PageWidth - Margin, _y + 14, SKTextAlign.Right, bold, _ink);
            _y += 30;
            _canvas.DrawText(doc.BranchLine, Margin, _y, SKTextAlign.Left, small, _muted);
            _canvas.DrawText(doc.DoctorDetails, PageWidth - Margin, _y, SKTextAlign.Right, small, _muted);
            _y += 10;
            _canvas.DrawLine(Margin, _y, PageWidth - Margin, _y, _rule);
            _y += 26;
            _canvas.DrawText(doc.Title.ToUpperInvariant(), Margin, _y, SKTextAlign.Left, title, _accent);
            _canvas.DrawText(doc.Date.ToString("dd MMM yyyy"), PageWidth - Margin, _y, SKTextAlign.Right, small, _muted);
            _y += 20;
            using var name = new SKFont(_bold, 12);
            _canvas.DrawText($"Patient: {doc.PatientName}", Margin, _y, SKTextAlign.Left, name, _ink);
            _y += 14;
            _canvas.DrawText(doc.PatientDetails, Margin, _y, SKTextAlign.Left, small, _muted);
            _y += 24;
        }

        private void Section(LetterSection s)
        {
            using var head = new SKFont(_bold, 11);
            using var body = new SKFont(_regular, 10.5f);
            if (s.Title.Length > 0)
            {
                Ensure(30);
                _canvas.DrawText(s.Title.ToUpperInvariant(), Margin, _y, SKTextAlign.Left, head, _accent);
                _y += 16;
            }
            if (s.Urdu && _urdu is not null)
            {
                UrduLines(s.Lines);
            }
            else
            {
                foreach (var line in s.Lines)
                    foreach (var w in Wrap(line, body, ContentWidth))
                    {
                        Ensure(15);
                        _canvas.DrawText(w, Margin, _y, SKTextAlign.Left, body, _ink);
                        _y += 15;
                    }
            }
            _y += 10;
        }

        /// <summary>Right-aligned, HarfBuzz-shaped Urdu, wrapped by shaped width.</summary>
        private void UrduLines(IEnumerable<string> lines)
        {
            using var font = new SKFont(_urdu!, 13);
            using var shaper = new SKShaper(_urdu!);
            foreach (var paragraph in lines)
            {
                var line = "";
                foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    var candidate = line.Length == 0 ? word : $"{line} {word}";
                    if (line.Length == 0 || shaper.Shape(candidate, font).Width <= ContentWidth) line = candidate;
                    else { DrawUrdu(line, font, shaper); line = word; }
                }
                if (line.Length > 0) DrawUrdu(line, font, shaper);
            }
        }

        private void DrawUrdu(string text, SKFont font, SKShaper shaper)
        {
            Ensure(20);
            var width = shaper.Shape(text, font).Width;
            _canvas.DrawShapedText(shaper, text, PageWidth - Margin - width, _y, SKTextAlign.Left, font, _ink);
            _y += 20;
        }

        private void Signature()
        {
            using var body = new SKFont(_regular, 10);
            using var bold = new SKFont(_bold, 10);
            Ensure(110);
            _y += 10;
            var x = PageWidth - Margin - 200;
            if (doc.SignaturePng is { Length: > 0 } png)
            {
                // Decode to a raster bitmap first: Skia's PDF backend crashes on lazily-decoded (encoded) images.
                // It reads the pixels when the page ends, so the bitmap must outlive this method.
                _keepAlive = SKBitmap.Decode(png);
                var image = _keepAliveImage = _keepAlive is null ? null : SKImage.FromBitmap(_keepAlive);
                if (image is not null)
                {
                    var h = 60f;
                    var w = Math.Min(200f, image.Width * h / Math.Max(1, image.Height));
                    _canvas.DrawImage(image, new SKRect(x, _y, x + w, _y + h), new SKSamplingOptions(SKFilterMode.Linear));
                    _y += h + 4;
                }
            }
            else _y += 30;
            _canvas.DrawLine(x, _y, PageWidth - Margin, _y, _rule);
            _y += 13;
            if (doc.SignatureLabel is { } label) _canvas.DrawText(label, PageWidth - Margin, _y, SKTextAlign.Right, bold, _ink);
            _y += 12;
            if (doc.SignatureCaption is { } caption) _canvas.DrawText(caption, PageWidth - Margin, _y, SKTextAlign.Right, body, _muted);
        }

        private void Footer()
        {
            using var small = new SKFont(_regular, 8);
            _canvas.DrawText($"Page {_page} · printed {DateTime.Now:dd MMM yyyy HH:mm}", Margin, PageHeight - 24, SKTextAlign.Left, small, _muted);
        }

        private static IEnumerable<string> Wrap(string text, SKFont font, float width)
        {
            foreach (var paragraph in text.Replace("\r", "").Split('\n'))
            {
                var line = "";
                foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    var candidate = line.Length == 0 ? word : $"{line} {word}";
                    if (font.MeasureText(candidate) <= width || line.Length == 0) line = candidate;
                    else { yield return line; line = word; }
                }
                yield return line;
            }
        }
    }
}
