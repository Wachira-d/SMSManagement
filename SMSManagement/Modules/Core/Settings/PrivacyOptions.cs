namespace SMSManagement.Modules.Core.Settings;

/// <summary>
/// Public-facing privacy-notice details for the platform operator — the PDPA
/// "data processor" / system provider. Shown on the root information page and
/// editable in /Admin/Settings. The per-campaign "data controller" is NOT set
/// here: it is the campaign-owning organisation and varies per campaign.
/// </summary>
public sealed class PrivacyOptions
{
    /// <summary>Operator / system-provider company name (the data processor).</summary>
    public string ProcessorName { get; init; } = string.Empty;

    /// <summary>Data-protection / DPO contact — an email address or phone.</summary>
    public string DpoContact { get; init; } = string.Empty;
}
