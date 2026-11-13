using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Notifications;
using SMSManagement.Modules.Notifications.Relay;

namespace SMSManagement.Tests.Notifications;

public sealed class EmailRelayReconcilerTests
{
    private readonly ServiceProvider _sp = EmailTestDb.Services();
    private readonly StubRelayHandler _http = new();
    private readonly FixedClock _clock = new();

    private AppDbContext Db() => _sp.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();

    private EmailRelayReconciler Build(AppDbContext db)
        => new(db, EmailTestDb.Client(_http, EmailTestDb.Configured(), _clock), _clock,
            NullLogger<EmailRelayReconciler>.Instance);

    private async Task<OutboundEmail> SeedAsync(OutboundEmailStatus status, TimeSpan age, string? relayId = "rid-1",
        string provider = "relay")
    {
        var db = Db();
        var row = new OutboundEmail
        {
            Provider = provider, Recipients = "x@example.com", Subject = "s", Status = status,
            RelayMessageId = relayId, CreatedAt = _clock.Now - age
        };
        db.OutboundEmails.Add(row);
        await db.SaveChangesAsync();
        return row;
    }

    [Fact]
    public async Task Pulls_status_for_stuck_queued_email()
    {
        var row = await SeedAsync(OutboundEmailStatus.Queued, TimeSpan.FromMinutes(20));
        _http.Respond = _ => StubRelayHandler.Json(HttpStatusCode.OK,
            """{"success":true,"data":{"id":"rid-1","status":"sent","attempts":1,"sentAt":"2026-11-13T04:41:00","providerMessageId":"<m@mx>"}}""");

        var changed = await Build(Db()).ReconcileAsync();

        changed.Should().Be(1);
        _http.Requests.Single().Uri.AbsolutePath.Should().Be("/api/v1/emails/rid-1");
        var saved = await Db().OutboundEmails.SingleAsync(x => x.Id == row.Id);
        saved.Status.Should().Be(OutboundEmailStatus.Sent);
        saved.StatusSource.Should().Be("pull");
        saved.LastPolledAt.Should().Be(_clock.Now);
    }

    [Fact]
    public async Task Skips_young_final_smtp_and_cooled_down_rows()
    {
        await SeedAsync(OutboundEmailStatus.Queued, TimeSpan.FromMinutes(1));                  // too young
        await SeedAsync(OutboundEmailStatus.Sent, TimeSpan.FromMinutes(20));                   // final
        await SeedAsync(OutboundEmailStatus.Queued, TimeSpan.FromMinutes(20), provider: "smtp");
        await SeedAsync(OutboundEmailStatus.Queued, TimeSpan.FromDays(4));                     // too old
        await SeedAsync(OutboundEmailStatus.Failed, TimeSpan.FromMinutes(20), relayId: null);  // never reached relay
        var db = Db();
        var cooled = await SeedAsync(OutboundEmailStatus.Queued, TimeSpan.FromMinutes(20));
        var c = await db.OutboundEmails.SingleAsync(x => x.Id == cooled.Id);
        c.LastPolledAt = _clock.Now.AddMinutes(-2);
        await db.SaveChangesAsync();

        await Build(Db()).ReconcileAsync();
        _http.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Abandoned_pending_row_is_marked_failed()
    {
        var row = await SeedAsync(OutboundEmailStatus.Pending, TimeSpan.FromHours(1), relayId: null);
        await Build(Db()).ReconcileAsync();
        var saved = await Db().OutboundEmails.SingleAsync(x => x.Id == row.Id);
        saved.Status.Should().Be(OutboundEmailStatus.Failed);
        saved.LastError.Should().Contain("interrupted");
        _http.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Relay_error_leaves_status_alone()
    {
        var row = await SeedAsync(OutboundEmailStatus.Queued, TimeSpan.FromMinutes(20));
        _http.Respond = _ => StubRelayHandler.Json(HttpStatusCode.Forbidden, """{"success":false,"message":"Missing scope: read"}""");
        var outcome = await Build(Db()).RefreshAsync(row.Id);
        outcome.Should().Contain("403").And.Contain("read");
        (await Db().OutboundEmails.SingleAsync(x => x.Id == row.Id)).Status.Should().Be(OutboundEmailStatus.Queued);
    }
}
