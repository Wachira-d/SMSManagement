using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace SMSManagement.Modules.Identity.Auth;

public interface IPasswordHasher
{
    /// <summary>Hash a password with the user's salt + the app pepper.</summary>
    string Hash(string password, string userSalt);

    /// <summary>Constant-time comparison — never use string equality on hashes.</summary>
    bool Verify(string password, string userSalt, string expectedHash);

    /// <summary>Generate a per-user salt for new cache rows.</summary>
    string NewSalt();
}

/// <summary>
/// SHA-256(password + userSalt + pepper) → lowercase hex.
/// Spec-compliant with the requirement document; the interface allows swapping
/// to Argon2id later without changing the authenticator.
/// </summary>
public sealed class Sha256PasswordHasher : IPasswordHasher
{
    private readonly string _pepper;

    public Sha256PasswordHasher(IOptions<UserCacheAuthOptions> opts)
    {
        _pepper = opts.Value.PasswordSalt;
        if (string.IsNullOrEmpty(_pepper))
            throw new InvalidOperationException(
                "UserCacheAuth:PasswordSalt is not configured (must be loaded from Key Vault).");
    }

    public string Hash(string password, string userSalt)
    {
        ArgumentNullException.ThrowIfNull(password);
        var combined = $"{password}{userSalt}{_pepper}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(combined));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public bool Verify(string password, string userSalt, string expectedHash)
    {
        var actual = Hash(password, userSalt);
        // Constant-time compare to defeat timing attacks.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(actual),
            Encoding.ASCII.GetBytes(expectedHash));
    }

    public string NewSalt()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes);
    }
}
