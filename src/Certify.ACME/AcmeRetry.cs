using Certes;
using Certify.Core.Services;

namespace Certify.ACME;

/// <summary>
/// Retry podpięty pod AcmeRetryPolicy z klasyfikatorami klienta:
/// - raw AcmeException (429/Retry-After, 5xx, rateLimited/serverInternal),
/// - Certes AcmeRequestException (to samo po Error.Type/Status).
/// 4xx inne niż 429 (np. unauthorized, malformed) NIE są ponawiane.
/// </summary>
public static class AcmeRetry
{
    public static (bool Retry, TimeSpan? ServerDelay, string Reason)? Classify(Exception ex) => ex switch
    {
        AcmeException a => ClassifyStatus(a.StatusCode, a.Type, a.RetryAfter),
        AcmeRequestException c => ClassifyStatus((int)c.Error.Status, c.Error.Type, null),
        _ => null
    };

    private static (bool Retry, TimeSpan? ServerDelay, string Reason)? ClassifyStatus(int status, string type, TimeSpan? retryAfter)
    {
        if (status == 429 || type.Contains("rateLimited", StringComparison.OrdinalIgnoreCase))
            return (true, retryAfter, $"limit CA (429){(retryAfter.HasValue ? $", Retry-After {(int)retryAfter.Value.TotalSeconds}s" : "")}");
        if (status >= 500 || type.Contains("serverInternal", StringComparison.OrdinalIgnoreCase))
            return (true, null, "blad serwera CA");
        return null;
    }

    public static Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> op, IProgress<string>? log, CancellationToken ct, string opName, int maxAttempts = AcmeRetryPolicy.MaxAttempts) =>
        AcmeRetryPolicy.ExecuteAsync(op, Classify, log, ct, opName, maxAttempts);

    public static Task ExecuteAsync(Func<CancellationToken, Task> op, IProgress<string>? log, CancellationToken ct, string opName, int maxAttempts = AcmeRetryPolicy.MaxAttempts) =>
        AcmeRetryPolicy.ExecuteAsync(op, Classify, log, ct, opName, maxAttempts);
}
