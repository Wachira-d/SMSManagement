using FluentAssertions;
using SMSManagement.Modules.Identity.Auth;

namespace SMSManagement.Tests.Auth;

/// <summary>
/// Locks in the BuildUrl overlap-stripping behaviour. A common operator
/// misconfig is putting "/api" in BOTH BaseUrl and AuthenticatePath, which
/// without the smart join produces "/api/api/..." and a 404 → the user sees
/// "Invalid username or password" with no obvious cause.
/// </summary>
public sealed class AuthenApiClientUrlTests
{
    [Theory]
    [InlineData("https://host",        "/api/ldap/authenticate", "https://host/api/ldap/authenticate")]
    [InlineData("https://host/",       "/api/ldap/authenticate", "https://host/api/ldap/authenticate")]
    [InlineData("https://host/api",    "/api/ldap/authenticate", "https://host/api/ldap/authenticate")]
    [InlineData("https://host/api/",   "/api/ldap/authenticate", "https://host/api/ldap/authenticate")]
    [InlineData("https://host/api",    "/ldap/authenticate",     "https://host/api/ldap/authenticate")]
    [InlineData("https://host/api",    "ldap/authenticate",      "https://host/api/ldap/authenticate")]
    [InlineData("https://HOST/API",    "/api/ldap/authenticate", "https://HOST/API/ldap/authenticate")]
    public void BuildUrl_strips_overlapping_path_prefix(string baseUrl, string path, string expected)
    {
        AuthenApiClient.BuildUrl(baseUrl, path).Should().Be(expected);
    }

    [Fact]
    public void BuildUrl_handles_empty_path()
    {
        AuthenApiClient.BuildUrl("https://host/api", "").Should().Be("https://host/api");
    }

    [Fact]
    public void BuildUrl_does_not_strip_when_no_overlap()
    {
        AuthenApiClient.BuildUrl("https://host/v2", "/api/ldap/authenticate")
            .Should().Be("https://host/v2/api/ldap/authenticate");
    }
}
