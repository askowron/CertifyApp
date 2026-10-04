namespace Certify.Core.Services;

/// <summary>
/// Ponowienia operacji ACME z exponential backoff + jitter (jak CTW: odpornosc
/// na limity i chwilowe bledy CA). Nigdy nie ponawia anulowania przez uzytkownika.
/// Klasyfikatory specyficzne dla klienta (Certes/raw) doczepia warstwa ACME
/// przez extraClassifier (Core nie zna tych typow).
/// </summary>
public static class AcmeRetryPolicy
{
    public const int MaxAttempts = 5;
    public static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(300);

    /// <summary>Czysta kalkulacja opoznienia (testowalna).</summary>
    public static TimeSpan ComputeDelay(int attempt, TimeSpan? serverRetryAfter = null)
    {
        if (serverRetryAfter.HasValue)
            return serverRetryAfter.Value <= TimeSpan.Zero ? TimeSpan.Zero
                : (serverRetryAfter.Value > MaxRetryAfter ? MaxRetryAfter : serverRetryAfter.Value);
        var exp = BaseDelay.TotalSeconds * Math.Pow(2, Math.Max(0, attempt));
        return TimeSpan.FromSeconds(Math.Min(exp, MaxDelay.TotalSeconds));
    }

    /// <summary>Jitter 0-1s zeby N instancji nie uderzalo rowno.</summary>
    public static TimeSpan WithJitter(TimeSpan delay) =>
        delay + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1000));

    /// <summary>Czy to anulowanie przez uzytkownika (nie ponawiamy).</summary>
    public static bool IsUserCancellation(Exception ex, CancellationToken ct) =>
        ct.IsCancellationRequested || (ex is OperationCanceledException oce && oce.CancellationToken.IsCancellationRequested);

    /// <summary>Sieciowe (HttpClient/timeout). Zwraca powod albo null.</summary>
    public static string? NetworkReason(Exception ex) => ex switch
    {
        HttpRequestException => "blad sieci",
        TimeoutException => "timeout",
        // TaskCanceledException z wlasnym tokenem HttpClient = timeout, nie user-cancel.
        OperationCanceledException => "timeout operacji",
        _ => null
    };

    public static async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        Func<Exception, (bool Retry, TimeSpan? ServerDelay, string Reason)?>? extraClassifier,
        IProgress<string>? log,
        CancellationToken ct,
        string opName,
        int maxAttempts = MaxAttempts)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await operation(ct);
            }
            catch (Exception ex) when (!IsUserCancellation(ex, ct))
            {
                var classified = extraClassifier?.Invoke(ex);
                string? reason = classified?.Reason ?? NetworkReason(ex);
                var serverDelay = classified?.ServerDelay;
                var retry = classified?.Retry ?? reason != null;

                if (!retry || attempt + 1 >= maxAttempts)
                    throw;

                var delay = WithJitter(ComputeDelay(attempt, serverDelay));
                log?.Report($"[Retry] {opName}: {reason} - ponowienie {attempt + 2}/{maxAttempts} za {(int)delay.TotalSeconds}s.");
                await Task.Delay(delay, ct);
            }
        }
    }

    public static Task ExecuteAsync(
        Func<CancellationToken, Task> operation,
        Func<Exception, (bool Retry, TimeSpan? ServerDelay, string Reason)?>? extraClassifier,
        IProgress<string>? log,
        CancellationToken ct,
        string opName,
        int maxAttempts = MaxAttempts) =>
        ExecuteAsync(async c => { await operation(c); return true; }, extraClassifier, log, ct, opName, maxAttempts);
}
