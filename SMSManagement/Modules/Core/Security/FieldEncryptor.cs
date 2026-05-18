using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace SMSManagement.Modules.Core.Security;

public sealed class EncryptionOptions
{
    /// <summary>32-byte (256-bit) data-encryption key, base64-encoded.
    /// Resolved at startup from KMS/Key Vault; never read from appsettings in prod.</summary>
    public string DataKeyBase64 { get; init; } = string.Empty;
}

/// <summary>
/// AES-GCM envelope encryption for PII at rest. Output format:
///   [12 bytes nonce][16 bytes tag][ciphertext]
/// </summary>
public sealed class FieldEncryptor
{
    private readonly byte[] _key;

    public FieldEncryptor(IOptions<EncryptionOptions> options)
    {
        var raw = Convert.FromBase64String(options.Value.DataKeyBase64);
        if (raw.Length != 32)
            throw new InvalidOperationException("Data key must be 32 bytes (AES-256-GCM).");
        _key = raw;
    }

    public byte[] Encrypt(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var ptBytes = System.Text.Encoding.UTF8.GetBytes(plaintext);
        var ct = new byte[ptBytes.Length];
        using var gcm = new AesGcm(_key, tag.Length);
        gcm.Encrypt(nonce, ptBytes, ct, tag);

        var output = new byte[nonce.Length + tag.Length + ct.Length];
        Buffer.BlockCopy(nonce, 0, output, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, output, nonce.Length, tag.Length);
        Buffer.BlockCopy(ct, 0, output, nonce.Length + tag.Length, ct.Length);
        return output;
    }

    public string Decrypt(byte[] envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.Length < 28) throw new CryptographicException("Ciphertext envelope too short.");
        var nonce = envelope.AsSpan(0, 12);
        var tag = envelope.AsSpan(12, 16);
        var ct = envelope.AsSpan(28);
        var pt = new byte[ct.Length];
        using var gcm = new AesGcm(_key, tag.Length);
        gcm.Decrypt(nonce, ct, tag, pt);
        return System.Text.Encoding.UTF8.GetString(pt);
    }
}
