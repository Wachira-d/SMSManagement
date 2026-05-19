using System.Net;
using System.Net.Http.Headers;
using SMSManagement.Modules.Core.Security;
using SMSManagement.Modules.Sms.Domain;

namespace SMSManagement.Modules.Sms.Providers;

public sealed class EtrackerOptions
{
    public string BaseUrl { get; set; } = "https://www.etracker.cc/bulksms/mesapi.aspx";
    /// <summary>Resolved from Key Vault at startup, OR from
    /// ProjectSmsProviderConfig (encrypted) per project.</summary>
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string DefaultSenderId { get; set; } = "Honda";
    public string DefaultType { get; set; } = "0";
}

/// <summary>
/// Etracker SMS dispatcher. Credentials are resolved per-request via
/// <see cref="IProviderConfigResolver"/> so each project can override its
/// own Etracker username/password without leaking into the global config.
/// </summary>
public sealed class EtrackerSmsProvider : ISmsProvider
{
    public string Name => "etracker";

    private readonly HttpClient _http;
    private readonly IProviderConfigResolver _configResolver;
    private readonly ILogger<EtrackerSmsProvider> _log;

    public EtrackerSmsProvider(
        HttpClient http,
        IProviderConfigResolver configResolver,
        ILogger<EtrackerSmsProvider> log)
    {
        _http = http;
        _configResolver = configResolver;
        _log = log;
    }

    public async Task<ProviderDispatchResult> DispatchAsync(SmsRequest request, CancellationToken ct)
    {
        // Per-project config merged with global defaults.
        var opts = await _configResolver.ResolveEtrackerAsync(request.ProjectId, ct);

        // Recipient normalisation (legacy did this inline with hard-coded "66" prefix).
        var normalised = NormaliseMsisdn(request.Recipient);

        // Build form-encoded body — no secrets in the URL.
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["type"]   = opts.DefaultType,
            ["to"]     = normalised,
            ["from"]   = request.SenderId ?? opts.DefaultSenderId,
            ["text"]   = request.Body,
            ["servid"] = request.ProjectId.ToString("N")
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, opts.BaseUrl) { Content = form };
        var basic = Convert.ToBase64String(
            System.Text.Encoding.ASCII.GetBytes($"{opts.Username}:{opts.Password}"));
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
        var parts = trimmed.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.LastOrDefault();
    }
}
