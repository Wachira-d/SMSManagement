using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SMSManagement.Modules.Core.Security;
using SMSManagement.Modules.Sms.Domain;

namespace SMSManagement.Modules.Sms.Providers;

public sealed class InfobipOptions
{
    /// <summary>e.g. https://abc123.api.infobip.com (per-account base URL).</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>API key — Infobip's preferred auth. Resolved from KMS at startup,
    /// OR from ProjectSmsProviderConfig (encrypted) per project.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Display sender (alpha or short code).</summary>
    public string DefaultSenderId { get; set; } = string.Empty;

    /// <summary>Whether to pull <c>GET /sms/1/reports?messageId=…</c> as a
    /// fallback when the DN webhook hasn't arrived. Off by default — the
    /// webhook is the primary mechanism and the pull is only useful when the
    /// callback is unreliable.</summary>
    public bool QueryEnabled { get; set; }
}

/// <summary>
/// Infobip SMS provider — Send SMS API v3 (<c>POST /sms/3/messages</c>).
/// Credentials are resolved per-request via <see cref="IProviderConfigResolver"/>
/// so each project can use its own Infobip account.
/// </summary>
public sealed class InfobipSmsProvider : ISmsProvider
{
    public string Name => "infobip";

    private readonly HttpClient _http;
    private readonly IProviderConfigResolver _configResolver;
    private readonly ILogger<InfobipSmsProvider> _log;

    public InfobipSmsProvider(
        HttpClient http,
        IProviderConfigResolver configResolver,
        ILogger<InfobipSmsProvider> log)
    {
        _http = http;
        _configResolver = configResolver;
        _log = log;
    }

    public async Task<ProviderDispatchResult> DispatchAsync(SmsRequest request, CancellationToken ct)
    {
        var opts = await _configResolver.ResolveInfobipAsync(request.ProjectId, ct);
        var to = NormaliseE164(request.Recipient);

        // Send SMS API v3: messages[].{sender, destinations[].to, content.text}.
        var payload = new
        {
            messages = new[]
            {
                new
                {
                    sender = request.SenderId ?? opts.DefaultSenderId,
                    destinations = new[] { new { to } },
                    content = new { text = request.Body }
                }
            }
        };

        var endpoint = $"{opts.BaseUrl.TrimEnd('/')}/sms/3/messages";
        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(payload)
        };
        // Header auth — Infobip's required scheme: "App <apiKey>".
        req.Headers.Authorization = new AuthenticationHeaderValue("App", opts.ApiKey);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        _log.LogInformation("Dispatching SMS provider=infobip project={ProjectId} to={MaskedTo}",
            request.ProjectId, PiiMasking.MaskPhone(to));

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        _log.LogInformation("Infobip response project={ProjectId} http={Http} body={Body}",
            request.ProjectId, (int)resp.StatusCode, raw);

        if (!resp.IsSuccessStatusCode)
        {
            if (resp.StatusCode is HttpStatusCode.RequestTimeout
                or HttpStatusCode.TooManyRequests
                or >= HttpStatusCode.InternalServerError)
                throw new HttpRequestException(
                    $"Transient Infobip failure {(int)resp.StatusCode}", null, resp.StatusCode);

            return new ProviderDispatchResult(false, null, ExtractHttpErrorCode(raw, resp.StatusCode), raw);
        }

        // Infobip returns HTTP 200 even when a message is rejected — the verdict
        // is the per-message status group, not the HTTP code.
        var outcome = ParseSendResponse(raw);
        if (outcome.Accepted)
            return new ProviderDispatchResult(true, outcome.MessageId, null, raw);

        _log.LogWarning("Infobip rejected SMS project={ProjectId} status={Status}",
            request.ProjectId, outcome.StatusName);
        return new ProviderDispatchResult(false, null, $"INFOBIP_{outcome.StatusName}", raw);
    }

    public async Task<ProviderTestResult> TestCredentialsAsync(
        Guid projectId, CancellationToken ct = default)
    {
        var opts = await _configResolver.ResolveInfobipAsync(projectId, ct);
        if (string.IsNullOrWhiteSpace(opts.BaseUrl) || string.IsNullOrWhiteSpace(opts.ApiKey))
            return new ProviderTestResult(false, "Base URL and API key are not configured.");

        // Probe with an invalid recipient ("0") — Infobip rejects it per-message
        // (or with HTTP 400), so nothing deliverable is sent. Only HTTP 401/403
        // indicates the API key itself is bad.
        var payload = new
        {
            messages = new[]
            {
                new
                {
                    sender = string.IsNullOrWhiteSpace(opts.DefaultSenderId)
                        ? "TEST" : opts.DefaultSenderId,
                    destinations = new[] { new { to = "0" } },
                    content = new { text = "connection test" }
                }
            }
        };

        try
        {
            using var req = new HttpRequestMessage(
                HttpMethod.Post, $"{opts.BaseUrl.TrimEnd('/')}/sms/3/messages")
            {
                Content = JsonContent.Create(payload)
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("App", opts.ApiKey);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);

            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new ProviderTestResult(false,
                    $"API key rejected (HTTP {(int)resp.StatusCode}) — check the API key and Base URL.");
            if ((int)resp.StatusCode >= 500)
                return new ProviderTestResult(false, $"Infobip returned HTTP {(int)resp.StatusCode}.");

            return new ProviderTestResult(true, "API key accepted — reached Infobip.");
        }
        catch (Exception ex)
        {
            return new ProviderTestResult(false, $"Cannot reach Infobip: {ex.Message}");
        }
    }

    /// <summary>Normalises a recipient to international format (no leading "+").
    /// A national Thai number (leading 0) is mapped to the 66 country code.</summary>
    private static string NormaliseE164(string input)
    {
        var digits = new string(input.Where(char.IsDigit).ToArray());
        if (digits.StartsWith("00"))                                  // 00 intl prefix
            digits = digits[2..];
        else if (digits.StartsWith('0') && digits.Length >= 9)        // TH national
            digits = "66" + digits[1..];
        return digits;
    }

    public sealed record InfobipSendOutcome(bool Accepted, string? MessageId, string StatusName);

    /// <summary>
    /// Parses a Send SMS v3 response. Infobip status groups:
    /// 1 PENDING, 2 UNDELIVERABLE, 3 DELIVERED, 4 EXPIRED, 5 REJECTED.
    /// A submission is "accepted" only for PENDING / DELIVERED.
    /// </summary>
    public static InfobipSendOutcome ParseSendResponse(string? raw)
    {
        var body = (raw ?? string.Empty).Trim();
        if (body.Length == 0) return new(false, null, "EMPTY_RESPONSE");

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("messages", out var messages)
                || messages.ValueKind != JsonValueKind.Array
                || messages.GetArrayLength() == 0)
                return new(false, null, "NO_MESSAGES");

            var m = messages[0];
            var messageId = m.TryGetProperty("messageId", out var mid) ? mid.GetString() : null;

            if (!m.TryGetProperty("status", out var status)
                || status.ValueKind != JsonValueKind.Object)
                // No status block — accept if a message id was issued.
                return new(messageId is not null, messageId, "NO_STATUS");

            var groupId = status.TryGetProperty("groupId", out var g)
                          && g.ValueKind == JsonValueKind.Number
                ? g.GetInt32() : -1;
            var groupName = status.TryGetProperty("groupName", out var gn)
                ? gn.GetString() : null;
            var name = status.TryGetProperty("name", out var n) ? n.GetString() : null;

            var accepted = groupId is 1 or 3
                || string.Equals(groupName, "PENDING", StringComparison.OrdinalIgnoreCase)
                || string.Equals(groupName, "DELIVERED", StringComparison.OrdinalIgnoreCase);

            return new(accepted, messageId, name ?? groupName ?? "UNKNOWN");
        }
        catch (JsonException)
        {
            return new(false, null, "UNPARSEABLE_RESPONSE");
        }
    }

    /// <summary>On an HTTP error, Infobip returns
    /// <c>{"requestError":{"serviceException":{"messageId":"...","text":"..."}}}</c>.
    /// Use that error id when present, else fall back to the HTTP status.</summary>
    private static string ExtractHttpErrorCode(string raw, HttpStatusCode http)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("requestError", out var re)
                && re.TryGetProperty("serviceException", out var se)
                && se.TryGetProperty("messageId", out var id)
                && id.GetString() is { Length: > 0 } code)
                return $"INFOBIP_{code}";
        }
        catch (JsonException) { /* not JSON — fall through */ }
        return $"HTTP_{(int)http}";
    }
}
