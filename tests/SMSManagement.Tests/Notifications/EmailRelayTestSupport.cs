using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Identity.Services;
using SMSManagement.Modules.Notifications;
using SMSManagement.Modules.Notifications.Relay;

namespace SMSManagement.Tests.Notifications;

internal sealed class AdminUser : IUserContext
{
    public Guid UserId => Guid.Empty;
    public bool IsAuthenticated => false;
    public bool IsSystemAdmin => true;
}

/// <summary>Scripted HTTP endpoint that records every request it sees.</summary>
internal sealed class StubRelayHandler : HttpMessageHandler
{
    public sealed record Seen(HttpMethod Method, Uri Uri, Dictionary<string, string> Headers, byte[] Body);

    public List<Seen> Requests { get; } = new();
    public Func<Seen, HttpResponseMessage> Respond { get; set; } = _ => Json(HttpStatusCode.OK, "{}");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var body = request.Content is null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(ct);
        var seen = new Seen(request.Method, request.RequestUri!, headers, body);
        Requests.Add(seen);
        return Respond(seen);
    }

    public static HttpResponseMessage Json(HttpStatusCode code, string json)
        => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>The relay's 202 envelope for an accepted send.</summary>
    public static HttpResponseMessage Accepted(string id, string status = "queued")
        => Json(HttpStatusCode.Accepted,
            $$"""{"success":true,"message":"Accepted","data":{"id":"{{id}}","status":"{{status}}","duplicate":false},"error":null,"timestamp":"2026-11-13T00:00:00Z"}""");
}

internal sealed class StubHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;
    public StubHttpClientFactory(HttpMessageHandler handler) => _handler = handler;
    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
}

internal sealed class FixedClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 11, 13, 5, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}

internal static class EmailTestDb
{
    /// <summary>A provider whose scopes all share one in-memory database, so
    /// the dispatcher's own scope and the test's assertions see the same rows.</summary>
    public static ServiceProvider Services(string? name = null)
    {
        name ??= Guid.NewGuid().ToString();
        var sc = new ServiceCollection();
        sc.AddSingleton<IUserContext, AdminUser>();
        sc.AddDbContext<AppDbContext>(o => o
            .UseInMemoryDatabase(name)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning)));
        return sc.BuildServiceProvider();
    }

    public static EmailRelayClient Client(StubRelayHandler handler, EmailRelayOptions opts, TimeProvider? clock = null)
        => new(new StubHttpClientFactory(handler), TestOptionsMonitor.Of(opts), clock ?? new FixedClock());

    public static EmailRelayOptions Configured(Action<EmailRelayOptionsBuilder>? tweak = null)
    {
        var b = new EmailRelayOptionsBuilder();
        tweak?.Invoke(b);
        return b.Build();
    }
}

/// <summary>EmailRelayOptions is init-only; this keeps tests readable.</summary>
internal sealed class EmailRelayOptionsBuilder
{
    public string BaseUrl = "https://relay.test/";
    public string ApiKey = "erk_test_key";
    public string? SigningSecret;
    public string? WebhookSecret = "whsec-test";
    public bool AllowUnsignedWebhooks;
    public string? SenderProfile;
    public string? FromAddress;
    public string? FromName;

    public EmailRelayOptions Build() => new()
    {
        BaseUrl = BaseUrl, ApiKey = ApiKey, SigningSecret = SigningSecret, WebhookSecret = WebhookSecret,
        AllowUnsignedWebhooks = AllowUnsignedWebhooks, SenderProfile = SenderProfile,
        FromAddress = FromAddress, FromName = FromName
    };
}
