using Renci.SshNet;

namespace SMSManagement.Modules.Ingestion.Sources;

/// <summary>
/// JSON shape of an SFTP <c>IngestionSourceSettings.EncryptedConfig</c>.
/// Either <see cref="PrivateKeyPem"/> (optionally passphrase-protected) or
/// <see cref="Password"/> must be supplied.
/// </summary>
public sealed class SftpConfig
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 22;
    public string Username { get; set; } = string.Empty;
    public string PrivateKeyPem { get; set; } = string.Empty;
    /// <summary>Optional passphrase for an encrypted private key.</summary>
    public string Passphrase { get; set; } = string.Empty;
    /// <summary>Used when no private key is supplied (password auth).</summary>
    public string Password { get; set; } = string.Empty;
    public string RemoteDirectory { get; set; } = "/incoming";
    public string FilePattern { get; set; } = "*.csv";

    /// <summary>How many times to attempt the connection before giving up for
    /// this poll — covers a transient first-attempt failure. Default 3.</summary>
    public int RetryAttempts { get; set; } = 3;
}

/// <summary>
/// Builds a <see cref="SftpClient"/> from an <see cref="SftpConfig"/>, picking
/// the auth method: private-key auth takes precedence when a key is supplied,
/// otherwise password auth. One of the two is required.
/// </summary>
public static class SftpClientFactory
{
    public static SftpClient Create(SftpConfig cfg)
    {
        if (!string.IsNullOrWhiteSpace(cfg.PrivateKeyPem))
        {
            // Catch the common mistake of pasting a public-key fingerprint
            // (ssh-keygen -l output) instead of the private key file.
            if (!cfg.PrivateKeyPem.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "privateKeyPem is not a private key. Paste the full PEM block " +
                    "starting with '-----BEGIN ... PRIVATE KEY-----'. The value supplied " +
                    "looks like a public-key fingerprint, which cannot be used to log in — " +
                    "use the private key file, or switch to password auth.");

            using var keyStream = new MemoryStream(
                System.Text.Encoding.UTF8.GetBytes(cfg.PrivateKeyPem));
            var keyFile = string.IsNullOrEmpty(cfg.Passphrase)
                ? new PrivateKeyFile(keyStream)
                : new PrivateKeyFile(keyStream, cfg.Passphrase);
            return new SftpClient(cfg.Host, cfg.Port, cfg.Username, keyFile);
        }

        if (!string.IsNullOrWhiteSpace(cfg.Password))
            return new SftpClient(cfg.Host, cfg.Port, cfg.Username, cfg.Password);

        throw new InvalidOperationException(
            "SFTP config must supply either privateKeyPem or password.");
    }
}
