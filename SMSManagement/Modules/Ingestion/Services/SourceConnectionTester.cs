using System.Text.Json;
using SMSManagement.Modules.Ingestion.Sources;
using SMSManagement.Modules.Ingestion.Utilities;

namespace SMSManagement.Modules.Ingestion.Services;

public sealed record SourceTestResult(bool Ok, string Message);

/// <summary>
/// Verifies an ingestion source binding can actually be reached, without
/// running the pipeline. Used by the "test connection" action so operators
/// can validate SFTP credentials/paths before saving a binding.
/// </summary>
public static class SourceConnectionTester
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    public static Task<SourceTestResult> TestAsync(
        string? sourceType, string configJson, CancellationToken ct)
        => sourceType?.ToUpperInvariant() switch
        {
            "SFTP" => TestSftpAsync(configJson, ct),
            null or "" => Task.FromResult(new SourceTestResult(false, "Source type is required.")),
            _ => Task.FromResult(new SourceTestResult(false,
                $"Connection test is only available for SFTP sources (got '{sourceType}').")),
        };

    private static async Task<SourceTestResult> TestSftpAsync(string configJson, CancellationToken ct)
    {
        SftpConfig cfg;
        try
        {
            cfg = JsonSerializer.Deserialize<SftpConfig>(configJson, JsonOpts)
                  ?? throw new InvalidOperationException("config was empty.");
        }
        catch (Exception ex)
        {
            return new SourceTestResult(false, $"Invalid SFTP config JSON: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(cfg.Host))
            return new SourceTestResult(false, "Host is required.");
        if (string.IsNullOrWhiteSpace(cfg.PrivateKeyPem) && string.IsNullOrWhiteSpace(cfg.Password))
            return new SourceTestResult(false, "Supply either privateKeyPem or password.");

        try
        {
            return await Task.Run(() =>
            {
                using var client = SftpClientFactory.Create(cfg);
                client.ConnectionInfo.Timeout = ConnectTimeout;
                client.Connect();
                try
                {
                    if (!client.Exists(cfg.RemoteDirectory))
                        return new SourceTestResult(false,
                            $"Connected to {cfg.Host}, but remote directory " +
                            $"'{cfg.RemoteDirectory}' was not found.");

                    var matches = client.ListDirectory(cfg.RemoteDirectory)
                        .Count(f => !f.IsDirectory && GlobMatcher.IsMatch(f.Name, cfg.FilePattern));
                    return new SourceTestResult(true,
                        $"Connected to {cfg.Host}. '{cfg.RemoteDirectory}' has {matches} " +
                        $"file(s) matching '{cfg.FilePattern}'.");
                }
                finally
                {
                    client.Disconnect();
                }
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new SourceTestResult(false, "Connection test was cancelled or timed out.");
        }
        catch (Exception ex)
        {
            // Auth failures, unknown host, bad key, etc. — surface the reason.
            return new SourceTestResult(false, $"Connection failed: {ex.Message}");
        }
    }
}
