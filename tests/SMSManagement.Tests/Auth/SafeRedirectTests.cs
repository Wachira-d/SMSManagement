using FluentAssertions;
using SMSManagement.Modules.Identity.Auth;

namespace SMSManagement.Tests.Auth;

public sealed class SafeRedirectTests
{
    [Theory]
    // Allowed
    [InlineData("/dashboard",              "/dashboard")]
    [InlineData("/projects/abc?x=1",       "/projects/abc?x=1")]
    // Disallowed → fallback
    [InlineData(null,                      "/")]
    [InlineData("",                        "/")]
    [InlineData("   ",                     "/")]
    [InlineData("//evil.com/x",            "/")]
    [InlineData("\\\\evil.com\\x",         "/")]
    [InlineData("http://evil.com/x",       "/")]
    [InlineData("https://evil.com/x",      "/")]
    [InlineData("javascript:alert(1)",     "/")]
    [InlineData("dashboard",               "/")] // missing leading slash
    public void Resolve_only_allows_local_paths(string? input, string expected)
        => SafeRedirect.Resolve(input).Should().Be(expected);

    [Fact]
    public void Resolve_uses_custom_fallback()
        => SafeRedirect.Resolve("http://evil.com", "/login").Should().Be("/login");
}
