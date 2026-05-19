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
///
/// Configuration validation is DEFERRED to first Encrypt/Decrypt call.
/// See <c>Sha256PasswordHasher</c> for the rationale — startup checks
/// fail-fast on missing secrets so the deferred throw is the safety net.
/// </summary>
public sealed class FieldEncryptor
{
    private readonly IOptions<EncryptionOptions> _options;
    private byte[]? _key;

    public FieldEncryptor(IOptions<EncryptionOptions> options) => _options = options;

    private byte[] Key
    {
        get
        {
            if (_key is not null) return _key;
            var raw = _options.Value.DataKeyBase64;
            if (string.IsNullOrWhiteSpace(raw))
                throw new InvalidOperationException(
                    "Encryption:DataKeyBase64 is not configured. " +
                    "In Development, set Secrets:AutoGenerateInDev=true to auto-generate.");
            var bytes = Convert.FromBase64String(raw);
            if (bytes.Length != 32)
                throw new InvalidOperationException(
                    $"Encryption:DataKeyBase64 must be 32 bytes; got {bytes.Length}.");
            return _key = bytes;
        }
    }

    public byte[] Encrypt(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var ptBytes = System.Text.Encoding.UTF8.GetBytes(plaintext);
        var ct = new byte[ptBytes.Length];
        using var gcm = new AesGcm(Key, tag.Length);
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
        using var gcm = new AesGcm(Key, tag.Length);
        gcm.Decrypt(nonce, ct, tag, pt);
        return System.Text.Encoding.UTF8.GetString(pt);
    }
}
