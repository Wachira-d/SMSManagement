using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using SMSManagement.Modules.Core.Security;
using SMSManagement.Modules.Sms.Domain;

namespace SMSManagement.Modules.Sms.Providers;

public sealed class EtrackerOptions
{
    public string BaseUrl { get; init; } = "https://www.etracker.cc/bulksms/mesapi.aspx";
    /// <summary>Resolved from Key Vault at startup, never from appsettings.</summary>
    public string Username { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public string DefaultSenderId { get; init; } = "Honda";
    public string DefaultType { get; init; } = "0";
}

/// <summary>
/// Modern replacement for the legacy <c>WebClient.DownloadString(buildUrlWithCreds…)</c>
/// dispatch path. Differences from legacy:
///  - async/await, uses IHttpClientFactory (pooled HttpMessageHandler).
///  - resilience pipeline (Polly) wired up in DI; this class just throws on failure.
///  - credentials sent via Basic auth header, not query string ⇒ no logging leak.
///  - never logs body or recipient (only masked form).
/// </summary>
public sealed class EtrackerSmsProvider : ISmsProvider
{
    public string Name => "etracker";

    private readonly HttpClient _http;
    private readonly EtrackerOptions _opts;
    private readonly ILogger<EtrackerSmsProvider> _log;

    public EtrackerSmsProvider(
        HttpClient http,
        IOptions<EtrackerOptions> opts,
        ILogger<EtrackerSmsProvider> log)
    {
        _http = http;
        _opts = opts.Value;
        _log = log;
    }

    public async Task<ProviderDispatchResult> DispatchAsync(SmsRequest request, CancellationToken ct)
    {
        // Recipient normalisation (legacy did this inline with hard-coded "66" prefix).
        var normalised = NormaliseMsisdn(request.Recipient);

        // Build form-encoded body — no secrets in the URL.
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["type"]   = _opts.DefaultType,
            ["to"]     = normalised,
            ["from"]   = request.SenderId ?? _opts.DefaultSenderId,
            ["text"]   = request.Body,
            ["servid"] = request.ProjectId.ToString("N")
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, _opts.BaseUrl) { Content = form };
        var basic = Convert.ToBase64String(
            System.Text.Encoding.ASCII.GetBytes($"{_opts.Username}:{_opts.Password}"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        _log.LogInformation(
            "Dispatching SMS provider={Provider} project={ProjectId} to={MaskedTo}",
            Name, request.ProjectId, PiiMasking.MaskPhone(normalised));

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            // Surface HTTP-level failures so the Polly pipeline can retry/break.
            if (resp.StatusCode is HttpStatusCode.RequestTimeout
                or HttpStatusCode.TooManyRequests
                or >= HttpStatusCode.InternalServerError)
            {
                throw new HttpRequestException(
                    $"Transient provider failure {(int)resp.StatusCode}", null, resp.StatusCode);
            }

            return new ProviderDispatchResult(false, null, $"HTTP_{(int)resp.StatusCode}", raw);
        }

        var providerId = ParseMessageId(raw);
        return new ProviderDispatchResult(true, providerId, null, raw);
    }

    private static string NormaliseMsisdn(string input)
    {
        var digits = new string(input.Where(char.IsDigit).ToArray());
        // Generic E.164-ish: leading 0 with no country code → Thailand (legacy behaviour preserved
        // but driven by config in real deployment).
        if (digits.StartsWith('0') && digits.Length >= 9) digits = "66" + digits[1..];
        return digits;
    }

    private static string? ParseMessageId(string body)
    {
        // Provider returns either an ID line or "OK <id>" — keep it tolerant.
        var trimmed = body.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        var parts = trimmed.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        return parts.LastOrDefault();
    }
}
