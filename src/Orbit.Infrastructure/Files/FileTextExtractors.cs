using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig;

namespace Orbit.Infrastructure.Files;

public interface IFileTextExtractor
{
    bool CanHandle(string extension);

    string? Extract(string fullPath, Stream stream);
}

public sealed class PlainTextExtractor : IFileTextExtractor
{
    private static readonly HashSet<string> Ext = new(StringComparer.OrdinalIgnoreCase)
    {
        "txt", "csv", "log", "md", "json",
    };

    public bool CanHandle(string extension) => Ext.Contains(extension);

    public string? Extract(string fullPath, Stream stream)
    {
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var text = reader.ReadToEnd();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}

public sealed class PdfTextExtractor : IFileTextExtractor
{
    public bool CanHandle(string extension) =>
        string.Equals(extension, "pdf", StringComparison.OrdinalIgnoreCase);

    public string? Extract(string fullPath, Stream stream)
    {
        using var doc = PdfDocument.Open(stream);
        var parts = new List<string>();
        foreach (var page in doc.GetPages())
        {
            var text = page.Text;
            if (!string.IsNullOrWhiteSpace(text))
            {
                parts.Add(text);
            }

            if (parts.Sum(p => p.Length) > 200_000)
            {
                break;
            }
        }

        return parts.Count == 0 ? null : string.Join('\n', parts);
    }
}

public sealed class DocxTextExtractor : IFileTextExtractor
{
    public bool CanHandle(string extension) =>
        string.Equals(extension, "docx", StringComparison.OrdinalIgnoreCase);

    public string? Extract(string fullPath, Stream stream)
    {
        using var doc = WordprocessingDocument.Open(stream, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null)
        {
            return null;
        }

        var text = body.InnerText;
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}

public sealed class XlsxTextExtractor : IFileTextExtractor
{
    public bool CanHandle(string extension) =>
        string.Equals(extension, "xlsx", StringComparison.OrdinalIgnoreCase);

    public string? Extract(string fullPath, Stream stream)
    {
        using var doc = SpreadsheetDocument.Open(stream, false);
        var workbook = doc.WorkbookPart;
        if (workbook is null)
        {
            return null;
        }

        var shared = workbook.SharedStringTablePart?.SharedStringTable;
        var parts = new List<string>();
        foreach (var sheetPart in workbook.WorksheetParts)
        {
            var sheetData = sheetPart.Worksheet.Elements<SheetData>().FirstOrDefault();
            if (sheetData is null)
            {
                continue;
            }

            foreach (var row in sheetData.Elements<Row>())
            {
                foreach (var cell in row.Elements<Cell>())
                {
                    var value = ReadCell(cell, shared);
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        parts.Add(value);
                    }
                }
            }

            if (parts.Sum(p => p.Length) > 200_000)
            {
                break;
            }
        }

        return parts.Count == 0 ? null : string.Join(' ', parts);
    }

    private static string? ReadCell(Cell cell, SharedStringTable? shared)
    {
        var raw = cell.CellValue?.InnerText;
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        if (cell.DataType?.Value == CellValues.SharedString && shared is not null
            && int.TryParse(raw, out var index)
            && index >= 0 && index < shared.ChildElements.Count)
        {
            return shared.ElementAt(index).InnerText;
        }

        return raw;
    }
}

public sealed class FileTextExtractionPipeline
{
    private readonly IReadOnlyList<IFileTextExtractor> _extractors =
    [
        new PlainTextExtractor(),
        new PdfTextExtractor(),
        new DocxTextExtractor(),
        new XlsxTextExtractor(),
    ];

    public string? TryExtract(string fullPath, string extension, Stream stream)
    {
        // MSG: filename-only until Phase 7.
        if (string.Equals(extension, "msg", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Images: metadata only (no OCR).
        if (extension is "png" or "jpg" or "jpeg" or "gif" or "bmp" or "webp" or "tif" or "tiff")
        {
            return null;
        }

        var extractor = _extractors.FirstOrDefault(e => e.CanHandle(extension));
        if (extractor is null)
        {
            return null;
        }

        try
        {
            if (stream.CanSeek)
            {
                stream.Position = 0;
            }

            return extractor.Extract(fullPath, stream);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
