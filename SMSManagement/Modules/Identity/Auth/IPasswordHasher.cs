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
///
/// Configuration validation is DEFERRED to first call (Hash/Verify/NewSalt)
/// rather than constructor. Reason: this service ends up in the DI graph of
/// pages like /Account/Login. If we threw in the constructor, simply browsing
/// to the login page would 500 — confusing because the user hasn't even tried
/// to log in yet. <c>StartupSecretsCheck</c> already fail-fasts at startup if
/// secrets are missing, so reaching this code path means either the startup
/// check was disabled or the secret was rotated to empty at runtime — both
/// edge cases where the deferred throw is the safer behaviour.
/// </summary>
public sealed class Sha256PasswordHasher : IPasswordHasher
{
    private readonly IOptions<UserCacheAuthOptions> _opts;

    public Sha256PasswordHasher(IOptions<UserCacheAuthOptions> opts) => _opts = opts;

    private string Pepper
    {
        get
        {
            var v = _opts.Value.PasswordSalt;
            if (string.IsNullOrEmpty(v))
                throw new InvalidOperationException(
                    "UserCacheAuth:PasswordSalt is not configured. " +
                    "In Development, set Secrets:AutoGenerateInDev=true to auto-generate. " +
                    "In Production, inject from Key Vault.");
            return v;
        }
    }

    public string Hash(string password, string userSalt)
    {
        ArgumentNullException.ThrowIfNull(password);
        var combined = $"{password}{userSalt}{Pepper}";
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
