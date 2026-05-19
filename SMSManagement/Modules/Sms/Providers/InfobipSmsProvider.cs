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
}

/// <summary>
/// Infobip SMS provider. Credentials resolved per-request via
/// <see cref="IProviderConfigResolver"/> so each project can use its own
/// Infobip account without sharing one global API key.
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

        // Infobip Send SMS API v2: POST /sms/2/text/advanced
        var payload = new
        {
            messages = new[]
            {
                new
                {
                    destinations = new[] { new { to } },
                    from = request.SenderId ?? opts.DefaultSenderId,
                    text = request.Body
                }
            }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{opts.BaseUrl}/sms/2/text/advanced")
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

        if (!resp.IsSuccessStatusCode)
        {
            if (resp.StatusCode is HttpStatusCode.RequestTimeout
                or HttpStatusCode.TooManyRequests
                or >= HttpStatusCode.InternalServerError)
                throw new HttpRequestException(
                    $"Transient Infobip failure {(int)resp.StatusCode}", null, resp.StatusCode);

            return new ProviderDispatchResult(false, null, $"HTTP_{(int)resp.StatusCode}", raw);
        }

        var providerMessageId = TryExtractMessageId(raw);
        return new ProviderDispatchResult(true, providerMessageId, null, raw);
    }

    private static string NormaliseE164(string input)
    {
        var digits = new string(input.Where(char.IsDigit).ToArray());
        // Strip leading 0 only if no country code present; routing layer should normalise upstream.
        if (digits.StartsWith("00")) digits = digits[2..];
        return digits;
    }

    private static string? TryExtractMessageId(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement
                .GetProperty("messages")[0]
                .GetProperty("messageId")
                .GetString();
        }
        catch { return null; }
    }
}
