using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SMSManagement.Modules.Notifications;
using SMSManagement.Modules.Notifications.Relay;

namespace SMSManagement.Tests.Integration;

/// <summary>
/// Resolves the email stack from the real container. The relay client's
/// resilience options are only validated when the named HttpClient is first
/// built, so a bad timeout combination would otherwise surface on the first
/// production send rather than in CI.
/// </summary>
public sealed class EmailWiringTests : IClassFixture<CampaignWebApplicationFactory>
{
    private readonly CampaignWebApplicationFactory _f;
    public EmailWiringTests(CampaignWebApplicationFactory f) => _f = f;

    [Fact]
    public void Email_services_resolve_and_share_one_dispatcher()
    {
        using var scope = _f.Services.CreateScope();
        var sp = scope.ServiceProvider;

        var sender = sp.GetRequiredService<IEmailSender>();
        sender.Should().BeOfType<EmailDispatcher>();
        sp.GetRequiredService<IEmailDispatcher>().Should().BeSameAs(sender);
        sp.GetServices<IEmailTransport>().Select(t => t.Name).Should().BeEquivalentTo("smtp", "relay");
        sp.GetRequiredService<IEmailRelayReconciler>().Should().NotBeNull();

        var build = () => sp.GetRequiredService<IHttpClientFactory>().CreateClient(EmailRelayClient.HttpClientName);
        build.Should().NotThrow();
    }
}
