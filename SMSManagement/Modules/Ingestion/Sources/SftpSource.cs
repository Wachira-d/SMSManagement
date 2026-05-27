using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using Renci.SshNet;

namespace SMSManagement.Modules.Ingestion.Sources;

public sealed class SftpOptions
{
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; } = 22;
    public string Username { get; init; } = string.Empty;
    /// <summary>Private key (PEM). Resolved from KMS — never password auth in prod.</summary>
    public string PrivateKeyPem { get; init; } = string.Empty;
    public string RemoteDirectory { get; init; } = "/incoming";
    public string FilePattern { get; init; } = "*.csv";
}

/// <summary>
/// Polled SFTP source. Connects with key auth, downloads each matching file to a local
/// temp path, defers parsing to <see cref="CsvUploadSource"/>, then archives the remote file.
/// </summary>
public sealed class SftpSource : IIngestionSource
{
    private readonly IOptionsMonitor<SftpOptions> _opts;
    private readonly ILogger<SftpSource> _log;

    public string Name => "SFTP";

    public SftpSource(IOptionsMonitor<SftpOptions> opts, ILogger<SftpSource> log)
    {
        _opts = opts;
        _log = log;
    }

    public async IAsyncEnumerable<IReadOnlyDictionary<string, string>> ReadAsync(
        IngestionContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        var opts = _opts.CurrentValue;
        using var keyStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(opts.PrivateKeyPem));
        var keyFile = new PrivateKeyFile(keyStream);
        using var client = new SftpClient(opts.Host, opts.Port, opts.Username, keyFile);
        client.Connect();

        try
        {
            var files = client.ListDirectory(opts.RemoteDirectory)
                .Where(f => !f.IsDirectory && Utilities.GlobMatcher.IsMatch(f.Name, opts.FilePattern))
                .ToList();

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                var tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".csv");
                await using (var fs = File.Create(tempPath))
                    client.DownloadFile(file.FullName, fs);

                _log.LogInformation("SFTP downloaded {Name} ({Size} bytes)", file.Name, file.Length);

                var inner = new CsvUploadSource();
                var innerContext = context with { SourceRef = tempPath };
                await foreach (var row in inner.ReadAsync(innerContext, ct))
                    yield return row;

                // Move to /archive — done after enumeration so caller controls commit timing.
                var archive = $"{opts.RemoteDirectory}/archive/{file.Name}";
                client.RenameFile(file.FullName, archive);
                File.Delete(tempPath);
            }
        }
        finally
        {
            client.Disconnect();
        }
    }

}
