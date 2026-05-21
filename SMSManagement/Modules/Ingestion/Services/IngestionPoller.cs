using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Renci.SshNet;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Core.Security;
using SMSManagement.Modules.Ingestion.Domain;
using SMSManagement.Modules.Ingestion.Sources;

namespace SMSManagement.Modules.Ingestion.Services;

/// <summary>
/// Walks every enabled source binding, fetches matching files, and runs the
/// ingestion pipeline against each. Per-source HostConfig is decrypted on
/// demand — secrets never live in memory longer than one poll.
///
/// Failures on one source are isolated: an exception logged, the loop
/// continues to the next source. The pipeline itself is idempotent
/// (FileHash UNIQUE), so re-polling the same file is a no-op.
/// </summary>
public sealed class IngestionPoller : IIngestionPoller
{
    private static readonly JsonSerializerOptions CaseInsensitiveJson =
        new() { PropertyNameCaseInsensitive = true };

    private readonly AppDbContext _db;
    private readonly FieldEncryptor _crypto;
    private readonly IIngestionPipeline _pipeline;
    private readonly TimeProvider _clock;
    private readonly ILogger<IngestionPoller> _log;

    public IngestionPoller(
        AppDbContext db,
        FieldEncryptor crypto,
        IIngestionPipeline pipeline,
        TimeProvider clock,
        ILogger<IngestionPoller> log)
    {
        _db = db;
        _crypto = crypto;
        _pipeline = pipeline;
        _clock = clock;
        _log = log;
    }

    public async Task PollAllAsync(CancellationToken ct = default)
    {
        var sources = await _db.IngestionSourceSettings
            .Where(s => s.Enabled)
            .ToListAsync(ct);

        foreach (var s in sources)
            await PollOneAsync(s, ct);
    }

    public async Task PollSourceAsync(Guid sourceId, CancellationToken ct = default)
    {
        var s = await _db.IngestionSourceSettings
            .FirstOrDefaultAsync(x => x.Id == sourceId, ct);
        if (s is null)
        {
            _log.LogWarning("Manual poll requested for unknown source {SourceId}.", sourceId);
            return;
        }

        _log.LogInformation("Manual poll triggered for source {SourceId} ({Type}).",
            s.Id, s.SourceType);
        await PollOneAsync(s, ct);
    }

    /// <summary>Polls one source, isolating failures so a bad binding never
    /// aborts a multi-source sweep.</summary>
    private async Task PollOneAsync(IngestionSourceSettings s, CancellationToken ct)
    {
        try
        {
            switch (s.SourceType.ToUpperInvariant())
            {
                case "SFTP":
                    await PollSftpAsync(s, ct);
                    break;
                case "MANUAL_CSV":
                case "REST":
                case "SHAREPOINT":
                case "CLOUD":
                    // Manual = pull from the upload portal; nothing to poll.
                    // The other two are stubs the user can implement; their
                    // adapters slot in here without touching the scheduler.
                    break;
                default:
                    _log.LogWarning("Unknown ingestion source type {Type}", s.SourceType);
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Poll failed for source {SourceId} ({Type})",
                s.Id, s.SourceType);
        }
    }

    private async Task PollSftpAsync(IngestionSourceSettings s, CancellationToken ct)
    {
        SftpConfig cfg;
        try
        {
            // Case-insensitive: the editor stores camelCase keys (host, port…).
            cfg = JsonSerializer.Deserialize<SftpConfig>(
                      _crypto.Decrypt(s.EncryptedConfig), CaseInsensitiveJson)
                  ?? throw new InvalidOperationException("Empty SFTP config.");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not decrypt SFTP config for source {SourceId}", s.Id);
            return;
        }

        SftpClient client;
        try
        {
            client = SftpClientFactory.Create(cfg);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "SFTP auth setup failed for source {SourceId}.", s.Id);
            return;
        }

        using (client)
        {
        // Retry the connection — the first attempt may hit a transient
        // network/server hiccup. Config errors are not retried (the client
        // was already built above).
        var attempts = Math.Clamp(cfg.RetryAttempts, 1, 10);
        var connected = false;
        for (var attempt = 1; attempt <= attempts && !connected; attempt++)
        {
            try
            {
                client.Connect();
                connected = true;
            }
            catch (Exception ex)
            {
                if (attempt >= attempts)
                {
                    _log.LogError(ex,
                        "SFTP connect failed for source {SourceId} after {Attempts} attempt(s).",
                        s.Id, attempts);
                    return;
                }
                var delaySec = 3 * attempt;
                _log.LogWarning(
                    "SFTP connect attempt {Attempt}/{Attempts} failed for source {SourceId}: "
                    + "{Error} — retrying in {Delay}s.",
                    attempt, attempts, s.Id, ex.Message, delaySec);
                await Task.Delay(TimeSpan.FromSeconds(delaySec), ct);
            }
        }
        try
        {
            var files = client.ListDirectory(cfg.RemoteDirectory)
                .Where(f => !f.IsDirectory && Utilities.GlobMatcher.IsMatch(f.Name, cfg.FilePattern))
                .ToList();

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();

                var temp = Path.Combine(Path.GetTempPath(),
                    $"sftp-{s.Id:N}-{Guid.NewGuid():N}{Path.GetExtension(file.Name)}");
                await using (var fs = File.Create(temp))
                    client.DownloadFile(file.FullName, fs);

                try
                {
                    var outcome = await _pipeline.IngestFileAsync(
                        s.ProjectId, s.Id, temp, forceReingest: false, ct);
                    _log.LogInformation(
                        "SFTP poll {Source} processed {File} batch={Batch} result={Result}",
                        s.Id, file.Name, outcome.BatchId, outcome.Result);

                    // Pipeline already archived/deleted the temp; only the remote
                    // file may still be present. Rename it into the remote /archive
                    // so we don't re-pick it up. The archived name carries a
                    // timestamp: daily exports reuse the same filename, so a
                    // plain rename would collide with a prior run's archived
                    // copy, throw, and leave the file to be polled forever.
                    var archiveDir = $"{cfg.RemoteDirectory.TrimEnd('/')}/archive";
                    TryCreateRemoteDir(client, archiveDir);
                    var stamp = _clock.GetUtcNow().ToString("yyyyMMddHHmmss");
                    var stem  = Path.GetFileNameWithoutExtension(file.Name);
                    var ext   = Path.GetExtension(file.Name);
                    var archived = $"{archiveDir}/{stem}.{stamp}{ext}";
                    try
                    {
                        client.RenameFile(file.FullName, archived);
                    }
                    catch (Exception ex)
                    {
                        // The data was ingested fine; only the move failed
                        // (permissions / archive dir). Log loudly — the file
                        // stays put and will be skipped as a duplicate on the
                        // next poll, but the operator needs to know it isn't
                        // being archived.
                        _log.LogError(ex,
                            "SFTP poll {Source}: ingested {File} but could NOT archive it "
                            + "to {Archived} — file left in place and will re-poll.",
                            s.Id, file.Name, archived);
                    }
                }
                finally
                {
                    if (File.Exists(temp)) File.Delete(temp);
                }
            }
        }
        finally
        {
            client.Disconnect();
        }
        }
    }

    private static void TryCreateRemoteDir(SftpClient client, string path)
    {
        try { if (!client.Exists(path)) client.CreateDirectory(path); }
        catch { /* idempotent best-effort */ }
    }
}
