using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using ExcelDataReader;
using Microsoft.EntityFrameworkCore;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Core.Security;
using SMSManagement.Modules.Coupon.Domain;

namespace SMSManagement.Modules.Coupon.Services;

/// <summary>
/// Bulk-imports real brand coupon codes from an uploaded CSV / XLSX, mints a
/// per-project public Token for each, and writes Coupon rows.
///
/// Duplicate handling — the operator's requirement: a real code is unique
/// SYSTEM-WIDE; a re-used code must be reported, not silently dropped. We
/// check both the existing DB (RealCodeHash unique index) AND within the
/// uploaded file itself, and return a per-row reason so the UI can show the
/// operator exactly which codes clashed.
/// </summary>
public interface ICouponImportService
{
    Task<CouponImportResult> ImportAsync(CouponImportRequest req, CancellationToken ct = default);
}

public sealed record CouponImportRequest(
    Guid ProjectId,
    Guid BrandId,
    string BatchName,
    decimal Value,
    DateTimeOffset? ExpiresAt,
    int TokenLength,
    string TokenAlphabet,
    string FileName,
    Stream FileContent,
    string? CodeColumn,        // null = first column
    Guid? CreatedByUserId);

public sealed record CouponImportResult(
    Guid? BatchId,
    int Accepted,
    int Rejected,
    IReadOnlyList<CouponImportRejection> Rejections);

public sealed record CouponImportRejection(int RowIndex, string Code, string Reason);

public sealed class CouponImportService : ICouponImportService
{
    private const int MaxRows = 100_000;

    private readonly AppDbContext _db;
    private readonly FieldEncryptor _crypto;
    private readonly ILogger<CouponImportService> _log;

    public CouponImportService(AppDbContext db, FieldEncryptor crypto, ILogger<CouponImportService> log)
    {
        _db = db;
        _crypto = crypto;
        _log = log;
    }

    public async Task<CouponImportResult> ImportAsync(CouponImportRequest req, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(req.FileName).ToLowerInvariant();
        var codes = ext is ".xlsx" or ".xls"
            ? ReadExcel(req.FileContent, req.CodeColumn)
            : ReadCsv(req.FileContent, req.CodeColumn);

        if (codes.Count == 0)
            return new CouponImportResult(null, 0, 0,
                new[] { new CouponImportRejection(0, "", "file_empty_or_no_code_column") });
        if (codes.Count > MaxRows)
            return new CouponImportResult(null, 0, 0,
                new[] { new CouponImportRejection(0, "", $"too_many_rows(max {MaxRows})") });

        var rejections = new List<CouponImportRejection>();

        // Hash every code up front; dedup within the file first.
        var seenInFile = new Dictionary<string, int>(StringComparer.Ordinal);
        var candidates = new List<(int rowIndex, string code, string hash)>();
        for (var i = 0; i < codes.Count; i++)
        {
            var code = codes[i].Trim();
            if (string.IsNullOrEmpty(code))
            {
                rejections.Add(new CouponImportRejection(i + 1, code, "blank_code"));
                continue;
            }
            var hash = HashCode(code);
            if (seenInFile.TryGetValue(hash, out var firstRow))
            {
                rejections.Add(new CouponImportRejection(i + 1, code,
                    $"duplicate_within_file(first seen row {firstRow})"));
                continue;
            }
            seenInFile[hash] = i + 1;
            candidates.Add((i + 1, code, hash));
        }

        // Cross-check against codes already in the system. One query, then a
        // set lookup — tells the operator which existing batch a clash lives in.
        var candidateHashes = candidates.Select(c => c.hash).ToList();
        var existing = await _db.Coupons
            .IgnoreQueryFilters()   // system-wide dedup spans every project
            .Where(c => candidateHashes.Contains(c.RealCodeHash))
            .Select(c => new { c.RealCodeHash, c.BatchId, c.ProjectId })
            .ToListAsync(ct);
        var existingByHash = existing
            .GroupBy(x => x.RealCodeHash)
            .ToDictionary(g => g.Key, g => g.First());

        var brandValid = await _db.CouponBrands
            .AnyAsync(x => x.Id == req.BrandId && x.ProjectId == req.ProjectId, ct);
        if (!brandValid)
            return new CouponImportResult(null, 0, codes.Count,
                new[] { new CouponImportRejection(0, "", "brand_not_found") });

        var alphabet = string.IsNullOrWhiteSpace(req.TokenAlphabet)
            ? "ABCDEFGHJKLMNPQRSTUVWXYZ23456789" : req.TokenAlphabet;
        var tokenLen = Math.Clamp(req.TokenLength, 4, 32);

        var batch = new CouponBatch
        {
            ProjectId = req.ProjectId,
            BrandId = req.BrandId,
            Name = req.BatchName,
            Value = req.Value,
            ExpiresAt = req.ExpiresAt,
            TokenLength = tokenLen,
            TokenAlphabet = alphabet,
            CreatedByUserId = req.CreatedByUserId
        };
        _db.CouponBatches.Add(batch);

        // Tokens must be unique per project — track the ones minted in THIS
        // import alongside what's already persisted so a within-batch clash
        // can't slip through before SaveChanges.
        var projectTokens = (await _db.Coupons
                .Where(c => c.ProjectId == req.ProjectId)
                .Select(c => c.Token)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        var accepted = 0;
        foreach (var (rowIndex, code, hash) in candidates)
        {
            if (existingByHash.TryGetValue(hash, out var clash))
            {
                rejections.Add(new CouponImportRejection(rowIndex, code,
                    clash.ProjectId == req.ProjectId
                        ? $"already_imported(batch {clash.BatchId:N})"
                        : "already_imported(another project)"));
                continue;
            }

            var token = MintToken(alphabet, tokenLen, projectTokens);
            projectTokens.Add(token);

            _db.Coupons.Add(new Domain.Coupon
            {
                ProjectId = req.ProjectId,
                BatchId = batch.Id,
                BrandId = req.BrandId,
                Token = token,
                EncryptedRealCode = _crypto.Encrypt(code),
                RealCodeHash = hash,
                Value = req.Value,
                ExpiresAt = req.ExpiresAt,
                Status = CouponStatus.Available
            });
            accepted++;
        }

        batch.TotalCount = accepted;
        if (accepted == 0)
        {
            // Nothing usable — don't leave an empty batch row behind.
            _db.CouponBatches.Remove(batch);
            await _db.SaveChangesAsync(ct);
            return new CouponImportResult(null, 0, rejections.Count, rejections);
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // A concurrent import committed an overlapping RealCodeHash (system-
            // wide unique) or Token (per-project unique) between our in-memory
            // dedup check and this save. Surface it as a clean rejection so the
            // operator re-uploads, instead of bubbling a raw 500.
            _log.LogWarning(ex,
                "Coupon import: save conflicted with a concurrent import (batch {BatchId}).",
                batch.Id);
            return new CouponImportResult(null, 0, codes.Count, new[]
            {
                new CouponImportRejection(0, "",
                    "import_conflict — a code or token collided with a concurrent import; please retry")
            });
        }
        _log.LogInformation(
            "Coupon import: batch={BatchId} accepted={Accepted} rejected={Rejected}",
            batch.Id, accepted, rejections.Count);
        return new CouponImportResult(batch.Id, accepted, rejections.Count, rejections);
    }

    private static string MintToken(string alphabet, int length, HashSet<string> taken)
    {
        // Buffers allocated once, reused each attempt (CA2014 — no stackalloc
        // inside the loop).
        Span<char> chars = stackalloc char[length];
        Span<byte> bytes = stackalloc byte[length];
        // Cryptographically-random draw; retry on the rare collision.
        for (var attempt = 0; attempt < 50; attempt++)
        {
            RandomNumberGenerator.Fill(bytes);
            for (var i = 0; i < length; i++)
                chars[i] = alphabet[bytes[i] % alphabet.Length];
            var token = new string(chars);
            if (!taken.Contains(token)) return token;
        }
        // Astronomically unlikely with a sane alphabet/length; fail loud.
        throw new InvalidOperationException(
            "Could not mint a unique coupon token — widen TokenLength / TokenAlphabet.");
    }

    private static string HashCode(string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code.Trim())));

    private static List<string> ReadCsv(Stream stream, string? codeColumn)
    {
        using var reader = new StreamReader(stream);
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            TrimOptions = TrimOptions.Trim,
            BadDataFound = null,
            MissingFieldFound = null
        });
        csv.Read();
        csv.ReadHeader();
        var headers = csv.HeaderRecord ?? Array.Empty<string>();
        var col = ResolveColumn(headers, codeColumn);

        var result = new List<string>();
        while (csv.Read())
        {
            var v = col is null ? csv.GetField(0) : csv.GetField(col);
            if (!string.IsNullOrWhiteSpace(v)) result.Add(v);
        }
        return result;
    }

    private static List<string> ReadExcel(Stream stream, string? codeColumn)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var reader = ExcelReaderFactory.CreateReader(stream);
        if (!reader.Read()) return new List<string>();

        var headers = new string[reader.FieldCount];
        for (var i = 0; i < reader.FieldCount; i++)
            headers[i] = reader.GetValue(i)?.ToString()?.Trim() ?? $"col_{i + 1}";
        var colIdx = 0;
        if (codeColumn is not null)
        {
            var found = Array.FindIndex(headers,
                h => string.Equals(h, codeColumn, StringComparison.OrdinalIgnoreCase));
            if (found >= 0) colIdx = found;
        }

        var result = new List<string>();
        while (reader.Read())
        {
            var v = reader.GetValue(colIdx)?.ToString();
            if (!string.IsNullOrWhiteSpace(v)) result.Add(v);
        }
        return result;
    }

    private static string? ResolveColumn(string[] headers, string? codeColumn)
    {
        if (codeColumn is null) return null;       // caller wants column 0
        return headers.FirstOrDefault(h =>
            string.Equals(h, codeColumn, StringComparison.OrdinalIgnoreCase));
    }
}
