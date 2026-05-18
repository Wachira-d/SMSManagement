using System.Globalization;
using System.Runtime.CompilerServices;
using CsvHelper;
using CsvHelper.Configuration;

namespace SMSManagement.Modules.Ingestion.Sources;

/// <summary>Streams rows from a CSV file path (typically a temp file written by the upload portal).</summary>
public sealed class CsvUploadSource : IIngestionSource
{
    public string Name => "MANUAL_CSV";

    public async IAsyncEnumerable<IReadOnlyDictionary<string, string>> ReadAsync(
        IngestionContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(context.SourceRef);
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            TrimOptions = TrimOptions.Trim,
            BadDataFound = null,
            MissingFieldFound = null
        };
        using var csv = new CsvReader(reader, config);

        await csv.ReadAsync().ConfigureAwait(false);
        csv.ReadHeader();
        var headers = csv.HeaderRecord ?? Array.Empty<string>();

        while (await csv.ReadAsync().ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in headers)
                row[h] = csv.GetField(h) ?? string.Empty;
            yield return row;
        }
    }
}
