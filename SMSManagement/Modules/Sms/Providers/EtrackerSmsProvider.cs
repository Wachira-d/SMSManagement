using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
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
    /// <summary>etracker "ServID" — the service id issued with the account
    /// (e.g. "MES01"). Required by the gateway.</summary>
    public string ServiceId { get; set; } = string.Empty;
    /// <summary>etracker "type": 0=ASCII, 5=Unicode. Left blank by default so
    /// the gateway auto-detects the encoding (correct for mixed Thai/English).
    /// Only set this to force a specific encoding.</summary>
    public string DefaultType { get; set; } = string.Empty;
}

/// <summary>
/// Etracker (MACROKIOSK BOLD.) SMS dispatcher for the mesapi endpoint.
/// Credentials are resolved per-request via <see cref="IProviderConfigResolver"/>
/// so each project can override its own etracker account without leaking into
/// the global config.
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

        var normalised = NormaliseMsisdn(request.Recipient);

        // etracker's mesapi authenticates via clear-text "user"/"pass" FORM
        // parameters — not an HTTP Basic auth header. "type" is omitted so the
        // gateway auto-detects ASCII vs Unicode (handles Thai); the form body
        // is UTF-8 URL-encoded, which is what mesapi expects when type is unset.
        var fields = new Dictionary<string, string>
        {
            ["user"]   = opts.Username,
            ["pass"]   = opts.Password,
            ["to"]     = normalised,
            ["from"]   = request.SenderId ?? opts.DefaultSenderId,
            ["text"]   = request.Body,
            ["servid"] = string.IsNullOrWhiteSpace(opts.ServiceId)
                ? request.ProjectId.ToString("N")
                : opts.ServiceId
        };
        if (!string.IsNullOrWhiteSpace(opts.DefaultType))
            fields["type"] = opts.DefaultType;

        // Tolerate a stray trailing "?" / whitespace in a hand-entered Base URL.
        var endpoint = opts.BaseUrl.Trim().TrimEnd('?');
        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(fields)
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        _log.LogInformation(
            "Dispatching SMS provider={Provider} project={ProjectId} to={MaskedTo}",
            Name, request.ProjectId, PiiMasking.MaskPhone(normalised));

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        // mesapi returns HTTP 200 even for logical rejections and carries the
        // real outcome in the body — log it verbatim for diagnosis.
        _log.LogInformation(
            "Etracker response project={ProjectId} http={Http} body={Body}",
            request.ProjectId, (int)resp.StatusCode, raw);

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

        var (status, msgId) = ParseEtrackerResponse(raw);
        if (status == "200")
            return new ProviderDispatchResult(true, msgId, null, raw);

        // Non-200 gateway status = logical rejection (invalid param, bad
        // account, blacklisted, …). Not transient — record it, don't retry.
        _log.LogWarning(
            "Etracker rejected SMS project={ProjectId} status={Status} ({Meaning})",
            request.ProjectId, status, DescribeStatus(status));
        return new ProviderDispatchResult(false, null, $"ETRACKER_{status}", raw);
    }

    private static string NormaliseMsisdn(string input)
    {
        var digits = new string(input.Where(char.IsDigit).ToArray());
        // Leading 0 with no country code → Thailand. mesapi wants the country
        // code without the "+" sign.
        if (digits.StartsWith('0') && digits.Length >= 9) digits = "66" + digits[1..];
        return digits;
    }

    /// <summary>
    /// Extracts (gatewayStatus, msgId) from a mesapi response. The gateway may
    /// reply as JSON (<c>{"MsgID","Msisdn","Status"}</c>), XML
    /// (<c>&lt;Result&gt;&lt;Status&gt;…</c>), or the classic comma format
    /// (<c>{MSISDN},{MsgID},{Status}</c>). Status "200" means accepted.
    /// </summary>
    public static (string Status, string? MsgId) ParseEtrackerResponse(string? raw)
    {
        var body = (raw ?? string.Empty).Trim();
        if (body.Length == 0) return ("EMPTY", null);

        if (body.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var status = JsonField(doc.RootElement, "Status");
                var msgId = JsonField(doc.RootElement, "MsgID");
                if (!string.IsNullOrEmpty(status))
                    return (status!, string.IsNullOrEmpty(msgId) ? null : msgId);
            }
            catch (JsonException) { /* fall through to other formats */ }
        }

        if (body.StartsWith('<'))
        {
            try
            {
                var xml = XDocument.Parse(body);
                var status = xml.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "Status")?.Value.Trim();
                var msgId = xml.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "MsgID")?.Value.Trim();
                if (!string.IsNullOrEmpty(status))
                    return (status!, string.IsNullOrEmpty(msgId) ? null : msgId);
            }
            catch (XmlException) { /* fall through */ }
        }

        // Classic format: {MSISDN},{MsgID},{Status}, optionally several
        // recipients joined by '|' and a trailing "|=balance,total". We send
        // one recipient, so take the first segment.
        var firstSegment = body.Split('|', 2)[0];
        var parts = firstSegment.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length >= 3)
            return (parts[2], string.IsNullOrEmpty(parts[1]) ? null : parts[1]);

        // A bare status code (e.g. "400").
        return (parts[^1], null);
    }

    private static string? JsonField(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in obj.EnumerateObject())
        {
            if (!string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            return p.Value.ValueKind == JsonValueKind.String
                ? p.Value.GetString()
                : p.Value.ToString();
        }
        return null;
    }

    /// <summary>Human-readable meaning of a mesapi gateway status code
    /// (API spec section 5.0) — used for log/diagnostics only.</summary>
    public static string DescribeStatus(string status) => status switch
    {
        "200" => "Successful",
        "400" => "Invalid Parameter — missing parameter or invalid field type",
        "401" => "Invalid Account — invalid username, password or ServID",
        "402" => "Invalid Account — insufficient credit",
        "403" => "Invalid Account — invalid client IP address",
        "404" => "Invalid SenderID length",
        "405" => "Invalid message type",
        "406" => "Invalid MSISDN length",
        "407" => "Message length exceeded",
        "408" => "Unauthorised sender",
        "409" => "System error — contact etracker support",
        "411" => "Blacklisted MSISDN / opted out",
        "412" => "Account suspended or terminated",
        "413" => "Broadcast not allowed at this time",
        "414" => "Account is inactive",
        "415" => "No active service",
        "416" => "Account not configured for this coverage",
        "427" => "Invalid broadcast title",
        "429" => "Invalid additional parameter",
        "431" => "Forbidden — account uses JWT auth, not clear-text credential",
        "433" or "434" or "435" => "Blocked — sending threshold breached",
        "500" => "Internal server error",
        _     => "Unknown gateway status",
    };
}
