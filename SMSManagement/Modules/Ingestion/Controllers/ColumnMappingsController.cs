using System.Globalization;
using System.Text.Json;
using CsvHelper;
using CsvHelper.Configuration;
using ExcelDataReader;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Identity.Domain;
using SMSManagement.Modules.Identity.Services;
using SMSManagement.Modules.Ingestion.Domain;

namespace SMSManagement.Modules.Ingestion.Controllers;

[ApiController]
[Authorize]
[Route("api/projects/{projectId:guid}/column-mappings")]
public sealed class ColumnMappingsController : ControllerBase
{
    // Preview-only upload cap. The full ingest goes through UploadController
    // with its own 100MB cap; this endpoint never persists the file, just
    // peeks at headers + a few rows. Keep small so a typo'd huge file
    // doesn't pin a thread parsing 1M rows for nothing.
    private const long MaxPreviewBytes = 10L * 1024 * 1024;
    private static readonly HashSet<string> AllowedExt =
        new(StringComparer.OrdinalIgnoreCase) { ".csv", ".xlsx", ".xls" };

    private static readonly HashSet<string> AllowedFields = new(StringComparer.OrdinalIgnoreCase)
        { "phone", "message", "url", "name", "email", "custom" };

    private static readonly HashSet<string> AllowedTransforms = new(StringComparer.OrdinalIgnoreCase)
        { "trim", "upper", "lower", "digits", "prefix_66", "hex" };

    private readonly AppDbContext _db;
    private readonly IProjectAccessService _access;

    public ColumnMappingsController(AppDbContext db, IProjectAccessService access)
    {
        _db = db;
        _access = access;
    }

    public sealed record UpsertRequest(
        string SourceColumn,
        string CanonicalField,
        string[]? TransformChain,
        int? JoinOrder = null,
        string? JoinSeparator = null);

    [HttpGet]
    public async Task<IActionResult> List(Guid projectId, CancellationToken ct)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Viewer, ct);
        var rows = await _db.ColumnMappings
            .Where(m => m.ProjectId == projectId)
            .OrderBy(m => m.CanonicalField).ThenBy(m => m.JoinOrder).ThenBy(m => m.SourceColumn)
            .Select(m => new
            {
                m.Id, m.SourceColumn, m.CanonicalField, m.JoinOrder, m.JoinSeparator,
                TransformChain = JsonSerializer.Deserialize<string[]>(m.TransformChainJson, (JsonSerializerOptions?)null)
            })
            .ToListAsync(ct);
        return Ok(rows);
    }

    [HttpPost]
    public async Task<IActionResult> Upsert(
        Guid projectId, [FromBody] UpsertRequest req, CancellationToken ct)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Admin, ct);

        if (string.IsNullOrWhiteSpace(req.SourceColumn))
            return BadRequest("SourceColumn is required.");
        if (!AllowedFields.Contains(req.CanonicalField))
            return BadRequest($"CanonicalField must be one of: {string.Join(", ", AllowedFields)}");

        var chain = req.TransformChain ?? Array.Empty<string>();
        foreach (var t in chain)
            if (!AllowedTransforms.Contains(t))
                return BadRequest($"Unknown transform '{t}'. Allowed: {string.Join(", ", AllowedTransforms)}");

        var existing = await _db.ColumnMappings
            .FirstOrDefaultAsync(m => m.ProjectId == projectId
                                   && m.SourceColumn == req.SourceColumn, ct);
        if (existing is null)
        {
            existing = new ColumnMapping
            {
                ProjectId = projectId,
                SourceColumn = req.SourceColumn,
            };
            _db.ColumnMappings.Add(existing);
        }
        existing.CanonicalField = req.CanonicalField.ToLowerInvariant();
        existing.TransformChainJson = JsonSerializer.Serialize(chain);
        existing.JoinOrder = req.JoinOrder ?? 0;
        // Empty string is a legitimate "no separator" (e.g. concatenate area+number);
        // null means "use the default single space".
        existing.JoinSeparator = req.JoinSeparator;

        await _db.SaveChangesAsync(ct);
        return Ok(new { existing.Id, existing.SourceColumn, existing.CanonicalField,
                        existing.JoinOrder, existing.JoinSeparator });
    }

    /// <summary>
    /// Parse a sample CSV/XLSX and return its headers + the first few rows
    /// — used by the Mapping UI so operators don't have to type column names.
    /// The file is read in memory, never written to disk, never ingested.
    /// </summary>
    [HttpPost("preview-headers")]
    [Authorize(Policy = "ingestion.upload")]
    [RequestSizeLimit(MaxPreviewBytes)]
    public async Task<IActionResult> PreviewHeaders(
        Guid projectId,
        IFormFile file,
        CancellationToken ct = default)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Member, ct);

        if (file is null || file.Length == 0)
            return BadRequest(new { Message = "No file uploaded." });
        if (file.Length > MaxPreviewBytes)
            return BadRequest(new { Message = $"Preview file too large (max {MaxPreviewBytes / (1024 * 1024)} MB)." });

        var ext = Path.GetExtension(file.FileName);
        if (!AllowedExt.Contains(ext))
            return BadRequest(new { Message = $"Unsupported extension {ext}. Allowed: {string.Join(", ", AllowedExt)}." });

        try
        {
            (string[] headers, List<Dictionary<string, string>> samples) = ext.ToLowerInvariant() switch
            {
                ".xlsx" or ".xls" => ReadExcelPreview(file),
                _                 => await ReadCsvPreviewAsync(file, ct)
            };

            var existing = await _db.ColumnMappings
                .Where(m => m.ProjectId == projectId)
                .Select(m => new { m.SourceColumn, m.CanonicalField })
                .ToListAsync(ct);
            var existingMap = existing.ToDictionary(
                e => e.SourceColumn, e => e.CanonicalField, StringComparer.OrdinalIgnoreCase);

            // Heuristic auto-suggest by header name. Operator can change anything;
            // this just saves clicks for the obvious cases.
            var suggestions = headers.Select(h => new
            {
                Header = h,
                AlreadyMapped = existingMap.TryGetValue(h, out var canon) ? canon : null,
                Suggested = existingMap.ContainsKey(h) ? null : Suggest(h)
            });

            return Ok(new
            {
                FileName = file.FileName,
                Headers = headers,
                Sample = samples,
                Suggestions = suggestions
            });
        }
        catch (Exception ex)
        {
            return BadRequest(new { Message = $"Failed to parse file: {ex.Message}" });
        }
    }

    private static async Task<(string[] headers, List<Dictionary<string, string>> samples)>
        ReadCsvPreviewAsync(IFormFile file, CancellationToken ct)
    {
        await using var stream = file.OpenReadStream();
        using var reader = new StreamReader(stream);
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            TrimOptions = TrimOptions.Trim,
            BadDataFound = null,
            MissingFieldFound = null
        });

        await csv.ReadAsync().ConfigureAwait(false);
        csv.ReadHeader();
        var headers = csv.HeaderRecord ?? Array.Empty<string>();

        var samples = new List<Dictionary<string, string>>(5);
        var n = 0;
        while (n < 5 && await csv.ReadAsync().ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in headers) row[h] = csv.GetField(h) ?? string.Empty;
            samples.Add(row);
            n++;
        }
        return (headers, samples);
    }

    private static (string[] headers, List<Dictionary<string, string>> samples) ReadExcelPreview(IFormFile file)
    {
        // Match ExcelUploadSource — same reader + codepage registration so
        // .xls (BIFF) workbooks parse on Linux/macOS builds. Static ctor of
        // ExcelUploadSource already registered, but registration is
        // idempotent so a second call is safe in case this controller is
        // hit before any ingestion has run.
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

        using var stream = file.OpenReadStream();
        using var reader = ExcelReaderFactory.CreateReader(stream);

        if (!reader.Read()) return (Array.Empty<string>(), new List<Dictionary<string, string>>());
        var headers = new string[reader.FieldCount];
        for (var i = 0; i < reader.FieldCount; i++)
            headers[i] = (reader.GetValue(i)?.ToString() ?? $"col_{i + 1}").Trim();

        var samples = new List<Dictionary<string, string>>(5);
        while (samples.Count < 5 && reader.Read())
        {
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var anyValue = false;
            for (var i = 0; i < headers.Length; i++)
            {
                if (string.IsNullOrEmpty(headers[i])) continue;
                var v = reader.GetValue(i)?.ToString() ?? string.Empty;
                if (v.Length > 0) anyValue = true;
                row[headers[i]] = v;
            }
            if (anyValue) samples.Add(row);
        }
        return (headers, samples);
    }

    // Pure naming heuristic — anything obvious lights up; everything else
    // shows nothing and the operator picks from the dropdown.
    private static string? Suggest(string header)
    {
        var lower = header.Trim().ToLowerInvariant();
        if (lower.Contains("phone") || lower.Contains("mobile")
            || lower.Contains("msisdn") || lower.Contains("tel")) return "phone";
        if (lower.Contains("email") || lower.Contains("e-mail") || lower == "mail") return "email";
        if (lower.Contains("url") || lower.Contains("link")) return "url";
        if (lower.Contains("name")) return "name";
        if (lower.Contains("message") || lower.Contains("msg") || lower.Contains("body")
            || lower.Contains("text")) return "message";
        return null;
    }

    [HttpDelete("{mappingId:guid}")]
    public async Task<IActionResult> Delete(
        Guid projectId, Guid mappingId, CancellationToken ct)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Admin, ct);
        var existing = await _db.ColumnMappings
            .FirstOrDefaultAsync(m => m.Id == mappingId && m.ProjectId == projectId, ct);
        if (existing is null) return NotFound();
        _db.ColumnMappings.Remove(existing);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}
