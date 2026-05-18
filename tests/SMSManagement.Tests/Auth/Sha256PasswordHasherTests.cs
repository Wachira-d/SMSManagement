using FluentAssertions;
using Microsoft.Extensions.Options;
using SMSManagement.Modules.Identity.Auth;

namespace SMSManagement.Tests.Auth;

public sealed class Sha256PasswordHasherTests
{
    private static Sha256PasswordHasher NewHasher(string pepper = "app-wide-pepper") =>
        new(Options.Create(new UserCacheAuthOptions { PasswordSalt = pepper }));

    [Fact]
    public void Throws_when_pepper_missing()
    {
        var act = () => new Sha256PasswordHasher(Options.Create(new UserCacheAuthOptions()));
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*PasswordSalt is not configured*");
    }

    [Fact]
    public void Same_inputs_produce_same_hash()
    {
        var hasher = NewHasher();
        var h1 = hasher.Hash("hunter2", "user-salt");
        var h2 = hasher.Hash("hunter2", "user-salt");
        h1.Should().Be(h2);
        h1.Should().HaveLength(64).And.MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void Different_users_with_same_password_have_different_hashes()
    {
        var hasher = NewHasher();
        var alice = hasher.Hash("hunter2", hasher.NewSalt());
        var bob   = hasher.Hash("hunter2", hasher.NewSalt());
        alice.Should().NotBe(bob);
    }

    [Fact]
    public void Different_pepper_invalidates_existing_hashes()
    {
        var salt = "s";
        var h1 = NewHasher("pepper-A").Hash("p", salt);
        var h2 = NewHasher("pepper-B").Hash("p", salt);
        h1.Should().NotBe(h2);
    }

    [Theory]
    [InlineData("password", "salt", true)]
    [InlineData("password", "different-salt", false)]
    [InlineData("Password", "salt", false)]
    [InlineData("",         "salt", false)]
    public void Verify_handles_match_and_mismatch(string testPwd, string testSalt, bool expected)
    {
        var hasher = NewHasher();
        var stored = hasher.Hash("password", "salt");
        hasher.Verify(testPwd, testSalt, stored).Should().Be(expected);
    }

    [Fact]
    public void NewSalt_returns_unique_hex_strings()
    {
        var hasher = NewHasher();
        var salts = Enumerable.Range(0, 50).Select(_ => hasher.NewSalt()).ToHashSet();
        salts.Should().HaveCount(50, "every salt must be distinct");
        salts.All(s => s.Length == 32 && s.All(c => "0123456789ABCDEF".Contains(c))).Should().BeTrue();
    }
}
