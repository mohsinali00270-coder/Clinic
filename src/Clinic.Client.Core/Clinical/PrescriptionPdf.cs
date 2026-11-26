using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace Clinic.Client.Core.Clinical;

/// <summary>
/// FR-RX-04 / FR-OFF-25: renders the prescription as an A4 PDF on the device, with no server. English via the
/// default typeface; Urdu shaped with HarfBuzz using a system font that covers Arabic script.
/// Unsigned encounters get a DRAFT watermark.
/// </summary>
public static class PrescriptionPdf
{
    private const float PageWidth = 595;   // A4 in points
    private const float PageHeight = 842;
    private const float Margin = 40;
    private const float ContentWidth = PageWidth - 2 * Margin;

    public static byte[] Render(PrescriptionDocument doc)
    {
        using var stream = new MemoryStream();
        using (var pdf = SKDocument.CreatePdf(stream, new SKDocumentPdfMetadata
               {
                   Title = $"Prescription – {doc.PatientName}",
                   Author = doc.DoctorName,
                   Creator = "Clinic",
                   Creation = DateTime.Now,
               }))
        {
            var writer = new Writer(pdf, doc);
            writer.Run();
            pdf.Close();
        }
        return stream.ToArray();
    }

    private sealed class Writer(SKDocument pdf, PrescriptionDocument doc)
    {
        private readonly SKTypeface _regular = SKTypeface.FromFamilyName("Arial") ?? SKTypeface.Default;
        private readonly SKTypeface _bold = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold) ?? SKTypeface.Default;
        private readonly SKTypeface? _urdu = SKFontManager.Default.MatchCharacter('ا'); // any font with Arabic script
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
            Patient();

            if (doc.Vitals.Count > 0) Section("Vitals", [string.Join("  ·  ", doc.Vitals)]);
            if (doc.Diagnoses.Count > 0) Section("Diagnosis", doc.Diagnoses);

            Medicines();

            if (doc.Advice.Count > 0) Section("Advice", doc.Advice);
            if (doc.FollowUp is { } f) Section("Follow-up", [$"Please come back on {f:dddd, dd MMM yyyy}."]);

            Signature();
            _canvas.Flush();
            pdf.EndPage();
        }

        private void NewPage()
        {
            if (_page > 0) { Footer(); pdf.EndPage(); }
            _canvas = pdf.BeginPage(PageWidth, PageHeight);
            _page++;
            _y = Margin;
            if (doc.IsDraft) Watermark();
        }

        private void Ensure(float height)
        {
            if (_y + height > PageHeight - Margin - 30) NewPage();
        }

        private void Watermark()
        {
            using var font = new SKFont(_bold, 110);
            using var paint = new SKPaint { Color = new SKColor(0xB4, 0x53, 0x09, 0x22), IsAntialias = true };
            _canvas.Save();
            _canvas.RotateDegrees(-35, PageWidth / 2, PageHeight / 2);
            _canvas.DrawText("DRAFT", PageWidth / 2, PageHeight / 2, SKTextAlign.Center, font, paint);
            _canvas.Restore();
        }

        private void Header()
        {
            using var title = new SKFont(_bold, 18);
            using var small = new SKFont(_regular, 9);
            using var doctor = new SKFont(_bold, 12);
            _canvas.DrawText(doc.ClinicName, Margin, _y + 16, SKTextAlign.Left, title, _accent);
            _canvas.DrawText(doc.DoctorName, PageWidth - Margin, _y + 14, SKTextAlign.Right, doctor, _ink);
            _y += 30;
            _canvas.DrawText(doc.BranchLine, Margin, _y, SKTextAlign.Left, small, _muted);
            _canvas.DrawText(doc.DoctorDetails, PageWidth - Margin, _y, SKTextAlign.Right, small, _muted);
            _y += 10;
            _canvas.DrawLine(Margin, _y, PageWidth - Margin, _y, _rule);
            _y += 18;
        }

        private void Patient()
        {
            using var name = new SKFont(_bold, 13);
            using var small = new SKFont(_regular, 10);
            _canvas.DrawText(doc.PatientName, Margin, _y, SKTextAlign.Left, name, _ink);
            _canvas.DrawText(doc.Date.ToString("dd MMM yyyy, HH:mm"), PageWidth - Margin, _y, SKTextAlign.Right, small, _muted);
            _y += 15;
            _canvas.DrawText(doc.PatientDetails, Margin, _y, SKTextAlign.Left, small, _muted);
            _y += 22;
        }

        private void Section(string title, IEnumerable<string> lines)
        {
            using var head = new SKFont(_bold, 11);
            using var body = new SKFont(_regular, 10);
            Ensure(30);
            _canvas.DrawText(title.ToUpperInvariant(), Margin, _y, SKTextAlign.Left, head, _accent);
            _y += 15;
            foreach (var line in lines)
            {
                foreach (var wrapped in Wrap(line, body, ContentWidth))
                {
                    Ensure(14);
                    _canvas.DrawText(wrapped, Margin, _y, SKTextAlign.Left, body, _ink);
                    _y += 14;
                }
            }
            _y += 8;
        }

        private void Medicines()
        {
            using var rx = new SKFont(_bold, 22);
            using var name = new SKFont(_bold, 11);
            using var body = new SKFont(_regular, 10);
            using var urdu = _urdu is null ? null : new SKFont(_urdu, 12);
            using var shaper = _urdu is null ? null : new SKShaper(_urdu);

            Ensure(40);
            _canvas.DrawText("Rx", Margin, _y + 6, SKTextAlign.Left, rx, _accent);
            _y += 22;
            if (doc.Lines.Count == 0)
            {
                _canvas.DrawText("No medicines prescribed.", Margin, _y, SKTextAlign.Left, body, _muted);
                _y += 20;
                return;
            }

            var n = 0;
            foreach (var line in doc.Lines)
            {
                n++;
                Ensure(60);
                _canvas.DrawText($"{n}.  {line.Medicine}", Margin, _y, SKTextAlign.Left, name, _ink);
                _y += 14;

                var detail = string.Join("  ·  ", new[] { line.Dose, line.FrequencyEnglish, line.Duration }.Where(s => !string.IsNullOrWhiteSpace(s)));
                _canvas.DrawText(detail, Margin + 16, _y, SKTextAlign.Left, body, _ink);

                // Urdu dosage, right-aligned (RTL) on the same line.
                if (doc.IncludeUrdu && urdu is not null && shaper is not null && line.FrequencyUrdu.Length > 0)
                {
                    var urduText = line.Duration.Length > 0 ? $"{line.FrequencyUrdu} — {line.Duration.Replace(" days", "")} دن" : line.FrequencyUrdu;
                    var shaped = shaper.Shape(urduText, urdu);
                    _canvas.DrawShapedText(shaper, urduText, PageWidth - Margin - shaped.Width, _y, SKTextAlign.Left, urdu, _ink);
                }
                _y += 14;

                if (!string.IsNullOrWhiteSpace(line.Instructions))
                {
                    foreach (var w in Wrap(line.Instructions!, body, ContentWidth - 16))
                    {
                        _canvas.DrawText(w, Margin + 16, _y, SKTextAlign.Left, body, _muted);
                        _y += 13;
                    }
                }
                _y += 8;
            }
            _y += 6;
        }

        private void Signature()
        {
            using var body = new SKFont(_regular, 10);
            using var bold = new SKFont(_bold, 10);
            Ensure(60);
            _y = Math.Max(_y + 30, _y);
            var x = PageWidth - Margin - 180;
            _canvas.DrawLine(x, _y, PageWidth - Margin, _y, _rule);
            _y += 13;
            _canvas.DrawText(doc.DoctorName, PageWidth - Margin, _y, SKTextAlign.Right, bold, _ink);
            _y += 12;
            _canvas.DrawText(doc.IsDraft ? "Not signed — draft" : "Signed electronically", PageWidth - Margin, _y, SKTextAlign.Right, body, _muted);
            Footer();
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
                    else
                    {
                        yield return line;
                        line = word;
                    }
                }
                yield return line;
            }
        }
    }
}
