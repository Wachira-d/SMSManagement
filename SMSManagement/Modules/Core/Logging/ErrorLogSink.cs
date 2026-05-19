using Microsoft.EntityFrameworkCore;
using Serilog.Core;
using Serilog.Events;
using SMSManagement.Infrastructure.Persistence;

namespace SMSManagement.Modules.Core.Logging;

/// <summary>
/// Serilog sink that persists Error/Fatal log events to the <c>ErrorLogs</c>
/// table via <see cref="AppDbContext"/>. Best-effort: a DB write failure
/// here must NOT throw out of the sink (would crash request pipelines that
/// emit error logs). On failure we fall back to the system Console so the
/// information is never silently lost.
///
/// Created via <see cref="ErrorLogSinkProvider"/> AFTER the DI container is
/// built, because Serilog is configured at host-build time but we need a
/// scope factory to resolve a fresh DbContext per event.
/// </summary>
public sealed class ErrorLogSink : ILogEventSink
{
    private readonly IServiceScopeFactory _scopeFactory;
    private const int MaxStackBytes = 8 * 1024;

    public ErrorLogSink(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    public void Emit(LogEvent logEvent)
    {
        if (logEvent.Level < LogEventLevel.Error) return;

        // Fire-and-forget — the Serilog sink contract is synchronous but the
        // DB write isn't. Tasks aren't awaited; exceptions are swallowed and
        // re-routed to Console so the sink itself can never crash anything.
        _ = Task.Run(() => PersistAsync(logEvent));
    }

    private async Task PersistAsync(LogEvent ev)
    {
        try
        {
            var row = Build(ev);
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ErrorLogs.Add(row);
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            // Last-resort: at least make sure the operator knows the sink itself
            // is broken. Don't recurse via Serilog (that would re-enter the sink).
            Console.Error.WriteLine(
                $"[ErrorLogSink] Persist failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static ErrorLog Build(LogEvent ev)
    {
        var props = ev.Properties;
        return new ErrorLog
        {
            CreatedAt           = ev.Timestamp,
            Level               = ev.Level.ToString(),
            SourceContext       = GetString(props, "SourceContext"),
            Message             = Trunc(ev.RenderMessage(), 2000) ?? string.Empty,
            ExceptionType       = ev.Exception?.GetType().FullName,
            ExceptionMessage    = Trunc(ev.Exception?.Message, 2000),
            ExceptionStackTrace = Trunc(ev.Exception?.ToString(), MaxStackBytes),
            RequestPath         = GetString(props, "RequestPath"),
            RequestMethod       = GetString(props, "RequestMethod"),
            CorrelationId       = GetString(props, "CorrelationId"),
            IpAddress           = GetString(props, "IpAddress"),
            UserId              = TryGetGuid(props, "UserId") ?? TryGetGuid(props, "app_user_id")
        };
    }

    private static string? GetString(IReadOnlyDictionary<string, LogEventPropertyValue> p, string key)
        => p.TryGetValue(key, out var v) && v is ScalarValue sv && sv.Value is not null
            ? sv.Value.ToString() : null;

    private static Guid? TryGetGuid(IReadOnlyDictionary<string, LogEventPropertyValue> p, string key)
    {
        var s = GetString(p, key);
        return Guid.TryParse(s, out var g) ? g : null;
    }

    private static string? Trunc(string? s, int max) =>
        s is { Length: > 0 } && s.Length > max ? s[..max] + "…[truncated]" : s;
}
