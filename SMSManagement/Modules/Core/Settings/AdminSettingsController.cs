using System.Net;
using System.Net.Http.Json;
using System.Net.Mail;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Core.Logging;
using SMSManagement.Modules.Identity.Auth;
using SMSManagement.Modules.Identity.Services;
using SMSManagement.Modules.Notifications;
using SMSManagement.Modules.Shortlink.Abuse;
using SMSManagement.Modules.Shortlink.Services;
using SMSManagement.Modules.Sms.Providers;
using SMSManagement.Modules.Sms.Webhooks;

namespace SMSManagement.Modules.Core.Settings;

/// <summary>
/// CRUD for the system-settings table plus a couple of connectivity tests
/// (AuthenAPI / SMTP). Read returns the *current effective* values from the
/// live IOptions snapshots merged with any per-key DB overrides, so admins
/// see what the running process is actually using.
///
/// Writes persist to <see cref="SystemSetting"/>. They DO NOT live-reload —
/// the response carries RestartRequired=true so the UI can warn the user.
/// </summary>
[ApiController]
[Authorize(Policy = "system_admin")]
[Route("api/admin/settings")]
public sealed class AdminSettingsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _me;
    private readonly IAuditLogger _audit;
    private readonly IConfiguration _config;

    // IOptionsSnapshot — recomputed per request, so a save + config reload is
    // reflected immediately by the next GET (no stale singleton snapshot).
    private readonly IOptionsSnapshot<AdminOptions> _admin;
    private readonly IOptionsSnapshot<UserCacheAuthOptions> _session;
    private readonly IOptionsSnapshot<AuthenApiOptions> _authenApi;
    private readonly IOptionsSnapshot<SmtpOptions> _smtp;
    private readonly IOptionsSnapshot<ShortlinkOptions> _shortlink;
    private readonly IOptionsSnapshot<ShortlinkAbuseOptions> _abuse;
    private readonly IOptionsSnapshot<EtrackerOptions> _etracker;
    private readonly IOptionsSnapshot<InfobipOptions> _infobip;
    private readonly IOptionsSnapshot<DlrWebhookOptions> _dlr;
    private readonly IOptionsSnapshot<PrivacyOptions> _privacy;

    private readonly IHttpClientFactory _httpFactory;
    private readonly IEmailSender _emailSender;
    private readonly ILogger<AdminSettingsController> _log;

    public AdminSettingsController(
        AppDbContext db, ICurrentUser me, IAuditLogger audit,
        IConfiguration config,
        IOptionsSnapshot<AdminOptions> admin,
        IOptionsSnapshot<UserCacheAuthOptions> session,
        IOptionsSnapshot<AuthenApiOptions> authenApi,
        IOptionsSnapshot<SmtpOptions> smtp,
        IOptionsSnapshot<ShortlinkOptions> shortlink,
        IOptionsSnapshot<ShortlinkAbuseOptions> abuse,
        IOptionsSnapshot<EtrackerOptions> etracker,
        IOptionsSnapshot<InfobipOptions> infobip,
        IOptionsSnapshot<DlrWebhookOptions> dlr,
        IOptionsSnapshot<PrivacyOptions> privacy,
        IHttpClientFactory httpFactory,
        IEmailSender emailSender,
        ILogger<AdminSettingsController> log)
    {
        _db = db; _me = me; _audit = audit; _config = config;
        _admin = admin; _session = session; _authenApi = authenApi;
        _smtp = smtp; _shortlink = shortlink; _abuse = abuse;
        _etracker = etracker; _infobip = infobip; _dlr = dlr; _privacy = privacy;
        _httpFactory = httpFactory; _emailSender = emailSender; _log = log;
    }

    /// <summary>
    /// Returns the live values from IOptions snapshots, grouped by category.
    /// Secret-shaped fields (Password, ApiKey, SigningKey, *Salt*, *Base64*)
    /// are masked — operators only need to see whether they're set, not the
    /// actual material.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var overrides = await _db.SystemSettings.AsNoTracking()
            .Select(s => new { s.Key, s.UpdatedAt })
            .ToListAsync(ct);

        return Ok(new
        {
            Sms = new
            {
                Etracker = MaskSecrets(new
                {
                    _etracker.Value.BaseUrl,
                    _etracker.Value.Username,
                    _etracker.Value.Password,
                    _etracker.Value.DefaultSenderId,
                    _etracker.Value.DefaultType
                }),
                Infobip = MaskSecrets(new
                {
                    _infobip.Value.BaseUrl,
                    _infobip.Value.ApiKey,
                    _infobip.Value.DefaultSenderId
                }),
                // Delivery-receipt webhooks. The provider posts back to one of
                // these URLs with the configured token — exposing the URL +
                // token-set status lets an operator wire it without code.
                Webhooks = MaskSecrets(new
                {
                    _dlr.Value.EtrackerDnToken,
                    _dlr.Value.InfobipDnToken
                }),
                WebhookBaseUrl = $"{Request.Scheme}://{Request.Host}/api/sms/dlr"
            },
            Smtp = MaskSecrets(new
            {
                _smtp.Value.Host, _smtp.Value.Port, _smtp.Value.EnableSsl,
                _smtp.Value.Username, _smtp.Value.Password,
                _smtp.Value.FromAddress, _smtp.Value.FromDisplayName,
                _smtp.Value.PickupDirectory
            }),
            Shortlink = new
            {
                _shortlink.Value.PublicBaseUrl,
                _shortlink.Value.SlugLength,
                DefaultAlphabet = ShortlinkService.DefaultSlugAlphabet,
                Abuse = new
                {
                    _abuse.Value.FailureThreshold,
                    _abuse.Value.WindowMinutes,
                    _abuse.Value.BlockDurationMinutes,
                    _abuse.Value.FailureRetentionDays
                }
            },
            Security = new
            {
                _session.Value.CacheDurationDays,
                _session.Value.MaxFailedAttempts,
                _session.Value.LockoutDurationMinutes,
                _session.Value.SessionTimeoutHours,
                _session.Value.RememberMeDurationDays,
                _session.Value.AllowOfflineFallback,
                _admin.Value.SystemAdminGroup
            },
            AuthenApi = MaskSecrets(new
            {
                _authenApi.Value.BaseUrl,
                _authenApi.Value.AuthenticatePath,
                _authenApi.Value.ApiKey,
                _authenApi.Value.TimeoutSeconds,
                _authenApi.Value.RetryAttempts,
                _authenApi.Value.AttemptTimeoutSeconds
            }),
            Privacy = new
            {
                _privacy.Value.ProcessorName,
                _privacy.Value.DpoContact
            },
            Overrides = overrides
        });
    }

    public sealed record UpsertItem(string Key, JsonElement Value, string Category);
    public sealed record UpsertRequest(IReadOnlyList<UpsertItem> Items);

    [HttpPut]
    public async Task<IActionResult> Upsert([FromBody] UpsertRequest req, CancellationToken ct)
    {
        if (req.Items is null || req.Items.Count == 0)
            return BadRequest(new { Message = "No items." });

        foreach (var item in req.Items)
        {
            var existing = await _db.SystemSettings.FirstOrDefaultAsync(s => s.Key == item.Key, ct);
            var rawJson = item.Value.GetRawText();
            if (existing is null)
            {
                _db.SystemSettings.Add(new SystemSetting
                {
                    Key = item.Key,
                    ValueJson = rawJson,
                    Category = item.Category,
                    UpdatedByUserId = _me.UserId
                });
            }
            else
            {
                existing.ValueJson = rawJson;
                existing.Category = item.Category;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
                existing.UpdatedByUserId = _me.UserId;
            }
        }
        await _db.SaveChangesAsync(ct);

        // Re-read the SystemSettings table into configuration so the new
        // values take effect immediately — IOptionsSnapshot / IOptionsMonitor
        // consumers pick them up on their next resolve, no restart needed.
        var reloaded = false;
        if (_config is IConfigurationRoot root)
        {
            root.Reload();
            reloaded = true;
        }

        // Audit a single rollup line — content is sensitive (could include
        // SMTP passwords, ApiKeys), so we record the *keys* only.
        await _audit.WriteAsync(new AuditEntry(
            _me.UserId, "admin.settings.update", "SystemSetting",
            string.Join(",", req.Items.Select(i => i.Key)),
            HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
            Request.Headers.UserAgent.ToString(),
            HttpContext.TraceIdentifier), ct);

        return Ok(new { Saved = req.Items.Count, RestartRequired = !reloaded });
    }

    /// <summary>
    /// Pokes the configured AuthenAPI base URL. We deliberately DON'T send
    /// credentials — this is a connectivity probe (DNS resolves, port open,
    /// TLS handshakes). A 4xx response IS a success for this test because it
    /// proves the host answered. Only network-level failures fail the probe.
    /// </summary>
    [HttpPost("test/authen-api")]
    public async Task<IActionResult> TestAuthenApi(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_authenApi.Value.BaseUrl))
            return BadRequest(new { Message = "AuthenApi.BaseUrl is not configured." });

        var url = AuthenApiClient.BuildUrl(_authenApi.Value.BaseUrl, _authenApi.Value.AuthenticatePath);
        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(Math.Max(1, _authenApi.Value.TimeoutSeconds));

        try
        {
            var started = DateTimeOffset.UtcNow;
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(new { username = "__probe__", password = "" })
            };
            if (!string.IsNullOrEmpty(_authenApi.Value.ApiKey))
                req.Headers.Add("X-API-Key", _authenApi.Value.ApiKey);

            using var resp = await http.SendAsync(req, ct);
            var elapsedMs = (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds;
            return Ok(new
            {
                Ok = true,
                Url = url,
                Status = (int)resp.StatusCode,
                ElapsedMs = elapsedMs,
                Note = "Host answered. A 4xx here is fine — it proves connectivity. Login is the end-to-end check."
            });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "AuthenAPI probe failed for {Url}", url);
            return Ok(new { Ok = false, Url = url, Error = ex.Message });
        }
    }

    /// <summary>
    /// Test that the DLR webhook URL for a provider is reachable with the
    /// configured token. Posts a dummy callback to the public endpoint — the
    /// dummy msgID won't match any real SMS (the DLR handler logs and
    /// returns 200), so a 200 here proves only that the URL and token are
    /// correct, which is what we want to verify.
    /// </summary>
    [HttpPost("test/dlr/{provider}")]
    public async Task<IActionResult> TestDlr(string provider, CancellationToken ct)
    {
        provider = (provider ?? string.Empty).ToLowerInvariant();
        if (provider != "etracker" && provider != "infobip")
            return BadRequest(new { Message = "provider must be 'etracker' or 'infobip'." });

        var token = provider == "etracker" ? _dlr.Value.EtrackerDnToken : _dlr.Value.InfobipDnToken;
        if (string.IsNullOrWhiteSpace(token))
            return Ok(new
            {
                Ok = false,
                Error = "Save a DN token for this provider first."
            });

        var baseUrl = $"{Request.Scheme}://{Request.Host}/api/sms/dlr/{provider}";
        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(10);

        try
        {
            HttpResponseMessage resp;
            if (provider == "etracker")
            {
                var url = baseUrl + "?token=" + Uri.EscapeDataString(token)
                        + "&msgID=admin-test&status=DELIVERED&statusDetail=admin+test";
                resp = await http.GetAsync(url, ct);
            }
            else
            {
                var url = baseUrl + "?token=" + Uri.EscapeDataString(token);
                var payload = """{"results":[{"messageId":"admin-test","status":{"groupName":"DELIVERED"}}]}""";
                using var body = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
                resp = await http.PostAsync(url, body, ct);
            }
            var status = (int)resp.StatusCode;
            var note = status switch
            {
                200 => "DN endpoint accepted the test callback. The dummy msgID intentionally "
                     + "doesn't match any real SMS — this proves the URL and token are correct.",
                401 => "Token rejected. The DN URL on the provider portal must carry the "
                     + "SAME token saved here.",
                _   => $"Endpoint responded with HTTP {status} (expected 200)."
            };
            return Ok(new
            {
                Ok = status == 200,
                Status = status,
                Url = baseUrl + "?token=••••",
                Note = note
            });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "DLR test failed for {Provider}", provider);
            return Ok(new
            {
                Ok = false,
                Url = baseUrl,
                Error = ex.Message,
                Note = "Could not reach the DN URL — check that the app's public host "
                     + "answers on HTTPS."
            });
        }
    }

    public sealed record TestSmtpBody(string To);

    [HttpPost("test/smtp")]
    public async Task<IActionResult> TestSmtp([FromBody] TestSmtpBody body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.To) || !body.To.Contains('@'))
            return BadRequest(new { Message = "Provide a valid 'To' address." });

        var opts = _smtp.Value;

        // Real diagnostic: do the SMTP send HERE so failures surface. The shared
        // IEmailSender deliberately swallows every error (alerting must never
        // abort a business transaction), so calling it would ALWAYS report
        // success — even when SMTP is unconfigured or the server rejects.
        if (string.IsNullOrWhiteSpace(opts.Host) && string.IsNullOrWhiteSpace(opts.PickupDirectory))
            return Ok(new { Ok = false, Error =
                "Smtp:Host is not configured. Fill in the SMTP section above, click Save, then test again." });

        try
        {
            using var mail = new MailMessage
            {
                From = new MailAddress(
                    string.IsNullOrWhiteSpace(opts.FromAddress)
                        ? "no-reply@example.com" : opts.FromAddress,
                    opts.FromDisplayName),
                Subject = "SMS Management — SMTP test",
                Body = $"Connectivity test from {Request.Host} at {DateTimeOffset.UtcNow:u}.",
                IsBodyHtml = false,
            };
            mail.To.Add(body.To);

            using var client = new SmtpClient();
            if (!string.IsNullOrWhiteSpace(opts.PickupDirectory))
            {
                Directory.CreateDirectory(opts.PickupDirectory);
                client.DeliveryMethod = SmtpDeliveryMethod.SpecifiedPickupDirectory;
                client.PickupDirectoryLocation = opts.PickupDirectory;
            }
            else
            {
                client.Host = opts.Host;
                client.Port = opts.Port;
                client.EnableSsl = opts.EnableSsl;
                if (!string.IsNullOrEmpty(opts.Username))
                    client.Credentials = new NetworkCredential(opts.Username, opts.Password);
            }

            await client.SendMailAsync(mail, ct);

            var via = string.IsNullOrWhiteSpace(opts.PickupDirectory)
                ? $"{opts.Host}:{opts.Port} (SSL={opts.EnableSsl})"
                : $"pickup directory {opts.PickupDirectory}";

            // "Accepted by the relay" is all System.Net.Mail can tell us — it
            // exposes neither a message-id nor the server's reply. Surface what
            // actually matters for "the mail never arrived": which From address
            // was used, and the deliverability checklist.
            var fromUsed = mail.From!.Address;
            var defaultFrom = string.IsNullOrWhiteSpace(opts.FromAddress)
                || string.Equals(opts.FromAddress, "no-reply@example.com",
                    StringComparison.OrdinalIgnoreCase);
            var note =
                "The relay ACCEPTED the message — this does NOT guarantee it reached the "
                + "inbox. If it doesn't arrive: (1) check the spam/junk folder; (2) make "
                + "sure the From-address domain is authorised to send through this relay "
                + "and passes SPF/DKIM; (3) confirm the relay may deliver to external "
                + "recipients (e.g. hotmail.com).";
            if (defaultFrom)
                note += " WARNING: the From address is the unconfigured default "
                      + "(no-reply@example.com) — set Smtp:FromAddress to a real address on "
                      + "your own domain, otherwise the message will almost certainly be "
                      + "rejected or junked.";

            return Ok(new
            {
                Ok = true,
                SentTo = body.To,
                From = fromUsed,
                Via = via,
                Note = note
            });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "SMTP test send failed to {To}", body.To);
            return Ok(new { Ok = false, Error = ex.Message });
        }
    }

    // ---------- helpers ----------

    private static object MaskSecrets(object source)
    {
        // Reflect once into an anonymous-object-shaped dict, replacing fields
        // whose name suggests a credential with a "•••• (set)" / null marker.
        var props = source.GetType().GetProperties();
        var dict = new Dictionary<string, object?>(props.Length, StringComparer.Ordinal);
        foreach (var p in props)
        {
            var name = p.Name;
            var val = p.GetValue(source);
            var looksSecret =
                name.EndsWith("Password", StringComparison.Ordinal)
             || name.EndsWith("ApiKey", StringComparison.Ordinal)
             || name.EndsWith("Key", StringComparison.Ordinal)
             || name.EndsWith("Token", StringComparison.Ordinal)
             || name.Contains("Secret", StringComparison.Ordinal)
             || name.Contains("Salt", StringComparison.Ordinal)
             || name.EndsWith("Base64", StringComparison.Ordinal);
            if (looksSecret && val is string s)
                dict[name] = string.IsNullOrEmpty(s) ? null : "•••• (set)";
            else
                dict[name] = val;
        }
        return dict;
    }
}
