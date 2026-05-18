using System.Reflection;
using FluentAssertions;
using SMSManagement.Infrastructure.Bootstrap;

namespace SMSManagement.Tests.Infrastructure;

/// <summary>
/// Verifies the hint mapper for SqlException error numbers. The full probe
/// flow needs a real SQL Server connection; this only exercises the static
/// translation table — kept tight because it's the part operators see.
/// </summary>
public sealed class StartupDatabaseGuardTests
{
    // HintFor is internal — invoke via reflection.
    private static string Invoke(int sqlNumber, string? user)
    {
        var m = typeof(StartupDatabaseGuard).GetMethod("HintFor",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        return (string)m.Invoke(null, new object?[] { sqlNumber, user })!;
    }

    private static string Redact(string? cs)
    {
        var m = typeof(StartupDatabaseGuard).GetMethod("Redact",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        return (string)m.Invoke(null, new object?[] { cs })!;
    }

    [Theory]
    [InlineData(18486, "LOCKED")]
    [InlineData(18487, "EXPIRED")]
    [InlineData(18488, "EXPIRED")]
    [InlineData(18456, "wrong password")]
    [InlineData(4060,  "Database does not exist")]
    [InlineData(40615, "Azure SQL firewall")]
    [InlineData(53,    "Cannot reach")]
    [InlineData(11001, "Cannot reach")]
    [InlineData(233,   "Pre-login handshake")]
    [InlineData(-2,    "timed out")]
    [InlineData(258,   "timed out")]
    [InlineData(99999, "Microsoft docs")]
    public void Hint_for_known_codes_returns_actionable_text(int sqlNumber, string expectedFragment)
        => Invoke(sqlNumber, "admin").Should().Contain(expectedFragment);

    [Fact]
    public void Hint_18487_includes_alter_login_command()
        => Invoke(18487, "admin").Should().Contain("ALTER LOGIN [admin]");

    [Fact]
    public void Redact_masks_password_in_connection_string()
    {
        var raw = "Server=localhost;Database=db;User Id=sa;Password=Secret!2026;TrustServerCertificate=true";
        var redacted = Redact(raw);
        redacted.Should().NotContain("Secret!2026");
        redacted.Should().Contain("***");
        // Other parts preserved. SqlConnectionStringBuilder normalises keys
        // ("Server" → "Data Source", "Database" → "Initial Catalog").
        redacted.Should().Contain("Data Source=localhost");
        redacted.Should().Contain("User ID=sa");
    }

    [Fact]
    public void Redact_handles_pwd_alias()
    {
        // Worst-case fallback path (un-parsable connection string).
        var raw = "fake;PWD=hidden;other=x";
        Redact(raw).Should().NotContain("hidden");
    }

    [Fact]
    public void Redact_handles_empty_and_null()
    {
        Redact(null).Should().Be("<empty>");
        Redact("").Should().Be("<empty>");
    }
}
