using System.Net.Http.Headers;
using System.Text.Json;
using SMSManagement.Modules.Sms.Domain;
using SMSManagement.Modules.Sms.Providers;

namespace SMSManagement.Modules.Sms.Services;

/// <summary>
/// Infobip delivery-status pull via the documented
/// <c>GET /sms/1/reports?messageId=…</c> endpoint. Each call returns the same
/// shape as the DN webhook (<c>{ "results": [ { "status": { "groupName" } } ] }</c>),
/// so the group-name → SmsStatus mapping is shared with the webhook path.
/// </summary>
public sealed class InfobipStatusQuery : IProviderStatusQuery
{
    private readonly HttpClient _http;
    private readonly IProviderConfigResolver _configResolver;
    private readonly ILogger<InfobipStatusQuery> _log;

    public string ProviderName => "infobip";

    public InfobipStatusQuery(
        HttpClient http,
        IProviderConfigResolver configResolver,
        ILogger<InfobipStatusQuery> log)
    {
        _http = http;
        _configResolver = configResolver;
        _log = log;
    }

    public bool IsEnabled(Guid projectId)
    {
        var opts = _configResolver.ResolveInfobipAsync(projectId, CancellationToken.None)
            .GetAwaiter().GetResult();
        return opts.QueryEnabled
            && !string.IsNullOrWhiteSpace(opts.BaseUrl)
            && !string.IsNullOrWhiteSpace(opts.ApiKey);
    }

    public async Task<StatusQueryResult> QueryAsync(
        Guid projectId, string providerMessageId, CancellationToken ct)
    {
        var opts = await _configResolver.ResolveInfobipAsync(projectId, ct);
        if (!opts.QueryEnabled) return new StatusQueryResult(null, null, null);

        var url = $"{opts.BaseUrl.TrimEnd('/')}/sms/1/reports"
                + $"?messageId={Uri.EscapeDataString(providerMessageId)}&limit=1";

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("App", opts.ApiKey);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning(
                    "Infobip status-query HTTP {Status} for msgId={MsgId} body={Body}",
                    (int)resp.StatusCode, providerMessageId, Trunc(body));
                return new StatusQueryResult(null, null, null);
            }

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("results", out var results)
                || results.ValueKind != JsonValueKind.Array
                || results.GetArrayLength() == 0)
                return new StatusQueryResult(null, null, null);

            var first = results[0];
            var group = first.TryGetProperty("status", out var st)
                && st.TryGetProperty("groupName", out var gn) ? gn.GetString() : null;
            if (string.IsNullOrWhiteSpace(group))
                return new StatusQueryResult(null, null, null);

            var mapped = DlrStatusMap.Map(group);
            var errorCode = mapped is SmsStatus.Failed or SmsStatus.Rejected or SmsStatus.Expired
                ? $"DN_{group.ToUpperInvariant()}"
                : null;
            // Infobip carries the actual handset-arrival time in doneAt;
            // sentAt is a fallback for in-flight reports.
            DateTimeOffset? carrierAt = null;
            if (first.TryGetProperty("doneAt", out var dn) && dn.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(dn.GetString(), out var parsedDone))
                carrierAt = parsedDone;
            else if (first.TryGetProperty("sentAt", out var sn) && sn.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(sn.GetString(), out var parsedSent))
                carrierAt = parsedSent;
            return new StatusQueryResult(mapped, errorCode, group.ToUpperInvariant(),
                CarrierDeliveredAt: carrierAt, RawPayload: first.GetRawText());
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Infobip status-query failed for msgId={MsgId}", providerMessageId);
            return new StatusQueryResult(null, null, null);
        }
    }

    private static string Trunc(string s) => s.Length > 200 ? s[..200] + "…" : s;
}
