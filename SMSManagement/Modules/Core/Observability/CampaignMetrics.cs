using System.Diagnostics.Metrics;

namespace SMSManagement.Modules.Core.Observability;

/// <summary>
/// Custom OpenTelemetry meter for the campaign platform. One Meter per
/// process; counters are tagged so a single time-series tells the whole
/// story (per-provider, per-result, per-source).
/// Exposed at <c>/metrics</c> via Prometheus exporter.
/// </summary>
public sealed class CampaignMetrics : IDisposable
{
    public const string MeterName = "Campaign.Platform";

    private readonly Meter _meter;

    public Counter<long> SmsDispatched { get; }
    public Counter<long> SmsDelivered { get; }
    public Counter<long> SmsFailed { get; }
    public Counter<long> LoginAttempts { get; }
    public Counter<long> ShortlinkClicks { get; }
    public Counter<long> WorkflowTransitions { get; }
    public Histogram<double> SmsDispatchLatencyMs { get; }

    public CampaignMetrics()
    {
        _meter = new Meter(MeterName, "1.0.0");
        SmsDispatched = _meter.CreateCounter<long>(
            "campaign.sms.dispatched",
            unit: "{messages}",
            description: "Total SMS attempts after the dispatcher returned a verdict.");
        SmsDelivered = _meter.CreateCounter<long>(
            "campaign.sms.delivered",
            unit: "{messages}",
            description: "Total SMS confirmed delivered by a DLR receipt.");
        SmsFailed = _meter.CreateCounter<long>(
            "campaign.sms.failed",
            unit: "{messages}",
            description: "Total SMS that reached a terminal Failed/Rejected state.");
        LoginAttempts = _meter.CreateCounter<long>(
            "campaign.auth.login_attempts",
            unit: "{attempts}",
            description: "Login attempts, tagged by outcome and source.");
        ShortlinkClicks = _meter.CreateCounter<long>(
            "campaign.shortlink.clicks",
            unit: "{clicks}",
            description: "Shortlink redirects (one per successful resolution).");
        WorkflowTransitions = _meter.CreateCounter<long>(
            "campaign.workflow.transitions",
            unit: "{transitions}",
            description: "Workflow state-machine transitions, tagged by trigger.");
        SmsDispatchLatencyMs = _meter.CreateHistogram<double>(
            "campaign.sms.dispatch_latency",
            unit: "ms",
            description: "End-to-end latency from EnqueueAsync to provider verdict.");
    }

    public void Dispose() => _meter.Dispose();
}
