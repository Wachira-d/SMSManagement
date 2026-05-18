using Serilog.Core;
using Serilog.Events;
using SMSManagement.Modules.Core.Security;

namespace SMSManagement.Modules.Core.Logging;

/// <summary>
/// Serilog enricher that walks every log event's properties and masks
/// any string value that looks like a phone number, email, or other PII.
/// Guarantees no raw recipient identifier reaches a log sink.
/// </summary>
public sealed class PiiMaskingEnricher : ILogEventEnricher
{
    private static readonly HashSet<string> AlwaysMaskNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "phone", "msisdn", "recipient", "to", "from", "email", "body", "messageBody", "text"
    };

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory pf)
    {
        foreach (var kvp in logEvent.Properties.ToArray())
        {
            if (kvp.Value is ScalarValue { Value: string s })
            {
                var masked = AlwaysMaskNames.Contains(kvp.Key)
                    ? PiiMasking.MaskPhone(s)
                    : PiiMasking.ScrubText(s);
                if (!ReferenceEquals(masked, s))
                    logEvent.AddOrUpdateProperty(pf.CreateProperty(kvp.Key, masked));
            }
        }
    }
}
