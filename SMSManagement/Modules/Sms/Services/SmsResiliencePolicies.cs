using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace SMSManagement.Modules.Sms.Services;

/// <summary>
/// Polly v8 resilience pipeline applied to every <c>HttpClient</c> backing an SMS provider.
/// Order matters: outermost = total timeout, then retry, then circuit breaker, then per-attempt timeout.
/// </summary>
public static class SmsResiliencePolicies
{
    public static ResiliencePipeline<HttpResponseMessage> Build(ILoggerFactory loggerFactory)
    {
        var log = loggerFactory.CreateLogger("SmsResilience");

        return new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddTimeout(new TimeoutStrategyOptions
            {
                Timeout = TimeSpan.FromSeconds(30),
                Name = "TotalTimeout"
            })
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = 3,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromMilliseconds(500),
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .Handle<HttpRequestException>()
                    .Handle<TimeoutRejectedException>()
                    .HandleResult(r => (int)r.StatusCode >= 500 || (int)r.StatusCode == 429),
                OnRetry = args =>
                {
                    log.LogWarning(
                        "SMS provider retry {Attempt} after {Delay}ms (outcome={Outcome})",
                        args.AttemptNumber, args.RetryDelay.TotalMilliseconds,
                        args.Outcome.Exception?.GetType().Name ?? args.Outcome.Result?.StatusCode.ToString());
                    return ValueTask.CompletedTask;
                }
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
            {
                FailureRatio = 0.5,
                MinimumThroughput = 10,
                SamplingDuration = TimeSpan.FromSeconds(30),
                BreakDuration = TimeSpan.FromSeconds(30),
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .Handle<HttpRequestException>()
                    .HandleResult(r => (int)r.StatusCode >= 500),
                OnOpened = args =>
                {
                    log.LogError("SMS provider circuit OPEN for {Duration}s",
                        args.BreakDuration.TotalSeconds);
                    return ValueTask.CompletedTask;
                },
                OnClosed = _ =>
                {
                    log.LogInformation("SMS provider circuit CLOSED");
                    return ValueTask.CompletedTask;
                }
            })
            .AddTimeout(TimeSpan.FromSeconds(10)) // per-attempt
            .Build();
    }
}
