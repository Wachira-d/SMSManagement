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
    }

    private async Task PollSftpAsync(IngestionSourceSettings s, CancellationToken ct)
    {
        SftpConfig cfg;
        try
        {
            cfg = JsonSerializer.Deserialize<SftpConfig>(_crypto.Decrypt(s.EncryptedConfig))
                  ?? throw new InvalidOperationException("Empty SFTP config.");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not decrypt SFTP config for source {SourceId}", s.Id);
            return;
        }

        using var keyStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(cfg.PrivateKeyPem));
        var keyFile = new PrivateKeyFile(keyStream);
        using var client = new SftpClient(cfg.Host, cfg.Port, cfg.Username, keyFile);

        client.Connect();
        try
        {
            var files = client.ListDirectory(cfg.RemoteDirectory)
                .Where(f => !f.IsDirectory && MatchesGlob(f.Name, cfg.FilePattern))
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
                    // so we don't re-pick it up.
                    var archiveDir = $"{cfg.RemoteDirectory.TrimEnd('/')}/archive";
                    TryCreateRemoteDir(client, archiveDir);
                    var archived = $"{archiveDir}/{file.Name}";
                    client.RenameFile(file.FullName, archived);
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

    private static bool MatchesGlob(string name, string pattern)
    {
        // Cheap "*.csv" support — full glob support belongs in a library.
        var ext = Path.GetExtension(pattern);
        return ext.Length == 0 || name.EndsWith(ext, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryCreateRemoteDir(SftpClient client, string path)
    {
        try { if (!client.Exists(path)) client.CreateDirectory(path); }
        catch { /* idempotent best-effort */ }
    }

    /// <summary>JSON shape of <c>IngestionSourceSettings.EncryptedConfig</c> for SFTP.</summary>
    private sealed class SftpConfig
    {
        public string Host { get; set; } = string.Empty;
        public int Port { get; set; } = 22;
        public string Username { get; set; } = string.Empty;
        public string PrivateKeyPem { get; set; } = string.Empty;
        public string RemoteDirectory { get; set; } = "/incoming";
        public string FilePattern { get; set; } = "*.csv";
    }
}
