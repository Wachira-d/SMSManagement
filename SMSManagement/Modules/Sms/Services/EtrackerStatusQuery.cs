using System.Net;
using SMSManagement.Modules.Sms.Domain;
using SMSManagement.Modules.Sms.Providers;

namespace SMSManagement.Modules.Sms.Services;

/// <summary>
/// MacroKiosk/etracker delivery-status pull. The exact endpoint varies per
/// account, so the URL is configurable on <see cref="EtrackerOptions.QueryUrl"/>
/// with two template tokens substituted at call time: <c>{user}</c>,
/// <c>{pass}</c>, and <c>{msgId}</c>. The response is parsed using the same
/// formats the DN webhook understands — comma-separated, JSON, or XML carrying
/// a <c>Status</c> field with the standard DN words (DELIVERED, UNDELIVERED,
/// ACCEPTED, …) — so a single status map keeps the two paths consistent.
/// </summary>
public sealed class EtrackerStatusQuery : IProviderStatusQuery
{
    private readonly HttpClient _http;
    private readonly IProviderConfigResolver _configResolver;
    private readonly ILogger<EtrackerStatusQuery> _log;

    public string ProviderName => "etracker";

    public EtrackerStatusQuery(
        HttpClient http,
        IProviderConfigResolver configResolver,
        ILogger<EtrackerStatusQuery> log)
    {
        _http = http;
        _configResolver = configResolver;
        _log = log;
    }

    public bool IsEnabled(Guid projectId)
    {
        // Resolve synchronously — IsEnabled is consulted in tight loops, and
        // the config resolver hits a small per-project cache (no network).
        var opts = _configResolver.ResolveEtrackerAsync(projectId, CancellationToken.None)
            .GetAwaiter().GetResult();
        return !string.IsNullOrWhiteSpace(opts.QueryUrl);
    }

    public async Task<StatusQueryResult> QueryAsync(
        Guid projectId, string providerMessageId, CancellationToken ct)
    {
        var opts = await _configResolver.ResolveEtrackerAsync(projectId, ct);
        if (string.IsNullOrWhiteSpace(opts.QueryUrl))
            return new StatusQueryResult(null, null, null);

        var url = opts.QueryUrl
            .Replace("{msgId}", Uri.EscapeDataString(providerMessageId))
            .Replace("{user}",  Uri.EscapeDataString(opts.Username ?? ""))
            .Replace("{pass}",  Uri.EscapeDataString(opts.Password ?? ""));

        string body;
        try
        {
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning(
                    "etracker status-query HTTP {Status} for msgId={MsgId} body={Body}",
                    (int)resp.StatusCode, providerMessageId, Trunc(body));
                return new StatusQueryResult(null, null, null);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "etracker status-query failed for msgId={MsgId}", providerMessageId);
            return new StatusQueryResult(null, null, null);
        }

        var (statusWord, _) = EtrackerSmsProvider.ParseEtrackerResponse(body);
        if (string.IsNullOrWhiteSpace(statusWord))
            return new StatusQueryResult(null, null, null);

        // Etracker returns either a DN word (DELIVERED / UNDELIVERED / …) or a
        // mesapi status code (200 / 4xx). The DN words map directly to our
        // SmsStatus; numeric codes mean "still in flight" (200=Accepted) or a
        // logical rejection — recorded as ErrorCode for the operator to see.
        var mapped = DlrStatusMap.Map(statusWord);
        var errorCode = mapped is SmsStatus.Failed or SmsStatus.Rejected or SmsStatus.Expired
            ? $"DN_{statusWord.ToUpperInvariant()}"
            : null;
        // mesapi pull responses carry only the status word — no separate
        // detail field. The word itself goes into StatusDetail so the report
        // shows the raw provider reason (DELIVERED / UNDELIVERED / …).
        return new StatusQueryResult(mapped, errorCode, statusWord.ToUpperInvariant());
    }

    private static string Trunc(string s) => s.Length > 200 ? s[..200] + "…" : s;
}
