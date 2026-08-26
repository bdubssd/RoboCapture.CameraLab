using System.Text.Json;

namespace RoboCapture.Core;

public sealed record StressTestOptions(int CaptureCount, int IntervalMs = 0);
public sealed record StressTestReport(
    int Attempts,
    int Successes,
    int Failures,
    int UnaccountedAttempts,
    TimeSpan Elapsed,
    TimeSpan? MinimumCaptureTime,
    TimeSpan? MaximumCaptureTime,
    TimeSpan? AverageCaptureTime,
    TimeSpan? MinimumTransferTime,
    TimeSpan? MaximumTransferTime,
    TimeSpan? AverageTransferTime,
    IReadOnlyDictionary<string, int> FailureReasons)
{
    public bool IsAccountedFor => Attempts == Successes + Failures && UnaccountedAttempts == 0;
    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
}

public sealed class StressTestEngine(ICameraDriver camera)
{
    public async Task<StressTestReport> RunAsync(StressTestOptions options, string destinationFolder,
        CancellationToken ct = default)
    {
        if (options.CaptureCount < 1) throw new ArgumentOutOfRangeException(nameof(options.CaptureCount));
        var started = DateTimeOffset.UtcNow;
        var results = new List<CaptureResult>(options.CaptureCount);

        for (var attempt = 1; attempt <= options.CaptureCount; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await camera.CaptureAsync(new CaptureRequest(
                Guid.NewGuid().ToString("N"), "STRESS", "STRESS", attempt, destinationFolder), ct));
            if (options.IntervalMs > 0) await Task.Delay(options.IntervalMs, ct);
        }

        var captures = results.Where(result => result.CaptureDuration.HasValue).Select(result => result.CaptureDuration!.Value).ToArray();
        var transfers = results.Where(result => result.TransferDuration.HasValue).Select(result => result.TransferDuration!.Value).ToArray();
        var reasons = results.Where(result => !result.Success).GroupBy(result => result.Error ?? "Unknown failure")
            .ToDictionary(group => group.Key, group => group.Count());
        return new StressTestReport(results.Count, results.Count(result => result.Success),
            results.Count(result => !result.Success), options.CaptureCount - results.Count,
            DateTimeOffset.UtcNow - started, Min(captures), Max(captures), Average(captures),
            Min(transfers), Max(transfers), Average(transfers), reasons);
    }

    private static TimeSpan? Min(TimeSpan[] values) => values.Length == 0 ? null : values.Min();
    private static TimeSpan? Max(TimeSpan[] values) => values.Length == 0 ? null : values.Max();
    private static TimeSpan? Average(TimeSpan[] values) => values.Length == 0 ? null : TimeSpan.FromTicks((long)values.Average(value => value.Ticks));
}