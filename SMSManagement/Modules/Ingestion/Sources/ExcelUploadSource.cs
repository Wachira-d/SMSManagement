using System.Runtime.CompilerServices;
using ExcelDataReader;

namespace SMSManagement.Modules.Ingestion.Sources;

/// <summary>
/// Streams rows from an .xlsx file uploaded via the portal. Mirrors
/// CsvUploadSource's contract — first row is the header, every subsequent
/// row produces a dictionary keyed by header name.
///
/// Only the first sheet is read. Empty trailing rows are skipped.
/// Headers are trimmed; cell values are coerced to strings (whatever the
/// cell's underlying type, we want a string the column mapper can transform).
/// </summary>
public sealed class ExcelUploadSource : IIngestionSource
{
    public string Name => "MANUAL_XLSX";

    static ExcelUploadSource()
    {
        // ExcelDataReader requires this for non-Windows .NET builds — sets up
        // the code page registry so .xls (BIFF) workbooks decode correctly.
        // Harmless for .xlsx but cheap and once-per-process.
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
    }

    public async IAsyncEnumerable<IReadOnlyDictionary<string, string>> ReadAsync(
        IngestionContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        // ExcelDataReader is synchronous; wrap on the thread pool so the
        // async iterator contract is honoured for the caller.
        await using var stream = File.OpenRead(context.SourceRef);
        using var reader = ExcelReaderFactory.CreateReader(stream);

        // First row of the first sheet is the header.
        if (!reader.Read()) yield break;

        var headers = new string[reader.FieldCount];
        for (var i = 0; i < reader.FieldCount; i++)
            headers[i] = (reader.GetValue(i)?.ToString() ?? string.Empty).Trim();

        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();

            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var anyValue = false;
            for (var i = 0; i < headers.Length; i++)
            {
                if (string.IsNullOrEmpty(headers[i])) continue;
                var raw = reader.GetValue(i);
                var value = raw?.ToString() ?? string.Empty;
                if (value.Length > 0) anyValue = true;
                row[headers[i]] = value;
            }

            // Skip fully-blank rows — Excel often pads with empties.
            if (!anyValue) continue;
            yield return row;
        }
    }
}
