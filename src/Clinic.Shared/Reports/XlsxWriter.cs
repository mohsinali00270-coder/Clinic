using System.IO.Compression;
using System.Security;
using System.Text;

namespace Clinic.Shared.Reports;

/// <summary>One worksheet: a title row, a header row and data rows (numbers stay numbers).</summary>
public sealed record Sheet(string Name, IReadOnlyList<string> Header, IReadOnlyList<IReadOnlyList<object?>> Rows, string? Title = null);

/// <summary>
/// FR-REP-03 export to Excel: a minimal Office Open XML workbook (inline strings, no styles) written with the BCL
/// only, so it works offline on every platform without a spreadsheet library.
/// </summary>
public static class XlsxWriter
{
    public static byte[] Write(IReadOnlyList<Sheet> sheets)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "[Content_Types].xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                string.Concat(sheets.Select((_, i) =>
                    $"<Override PartName=\"/xl/worksheets/sheet{i + 1}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>")) +
                "</Types>");
            Add(zip, "_rels/.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                "</Relationships>");
            Add(zip, "xl/workbook.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>" +
                string.Concat(sheets.Select((s, i) => $"<sheet name=\"{Esc(SheetName(s.Name, i))}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>")) +
                "</sheets></workbook>");
            Add(zip, "xl/_rels/workbook.xml.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                string.Concat(sheets.Select((_, i) =>
                    $"<Relationship Id=\"rId{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i + 1}.xml\"/>")) +
                "</Relationships>");
            for (var i = 0; i < sheets.Count; i++) Add(zip, $"xl/worksheets/sheet{i + 1}.xml", SheetXml(sheets[i]));
        }
        return ms.ToArray();
    }

    private static string SheetXml(Sheet s)
    {
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
        var r = 1;
        if (s.Title is { } title) Row(sb, r++, [title]);
        Row(sb, r++, s.Header.Cast<object?>().ToList());
        foreach (var row in s.Rows) Row(sb, r++, row);
        sb.Append("</sheetData></worksheet>");
        return sb.ToString();
    }

    private static void Row(StringBuilder sb, int r, IReadOnlyList<object?> cells)
    {
        sb.Append($"<row r=\"{r}\">");
        for (var c = 0; c < cells.Count; c++)
        {
            var reference = $"{Column(c)}{r}";
            switch (cells[c])
            {
                case null: break;
                case int or long or decimal or double or float:
                    sb.Append($"<c r=\"{reference}\"><v>{Convert.ToString(cells[c], System.Globalization.CultureInfo.InvariantCulture)}</v></c>");
                    break;
                case DateTime d:
                    sb.Append($"<c r=\"{reference}\" t=\"inlineStr\"><is><t>{d:yyyy-MM-dd HH:mm}</t></is></c>");
                    break;
                default:
                    sb.Append($"<c r=\"{reference}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{Esc(cells[c]!.ToString()!)}</t></is></c>");
                    break;
            }
        }
        sb.Append("</row>");
    }

    private static string Column(int index)
    {
        var name = "";
        for (var n = index + 1; n > 0; n = (n - 1) / 26) name = (char)('A' + (n - 1) % 26) + name;
        return name;
    }

    private static string SheetName(string name, int i)
    {
        var clean = new string(name.Where(ch => ch is not ('\\' or '/' or '?' or '*' or '[' or ']' or ':')).ToArray()).Trim();
        var n = clean.Length == 0 ? $"Sheet{i + 1}" : clean;
        return n.Length > 31 ? n[..31] : n;
    }

    private static string Esc(string s) => SecurityElement.Escape(s) ?? "";

    private static void Add(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Fastest);
        using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        w.Write(content);
    }
}
