namespace RoboCapture.Core;

public sealed class SimulatedCameraDriver : ICameraDriver
{
    private int _counter;
    private readonly Random _random;
    private bool _disconnectNext;
    public string DriverId => "simulator.v1";
    public CameraConnectionState State { get; private set; } = CameraConnectionState.Disconnected;
    public CameraInfo? Info { get; private set; }
    public event Action<CameraEvent>? Event;
    public double FailureRate { get; set; } = 0.0;
    public double TransferFailureRate { get; set; } = 0.0;
    public int CaptureLatencyMs { get; set; } = 350;
    public int TransferLatencyMs { get; set; } = 25;
    public int? CaptureTimeoutMs { get; set; }
    public int? TransferTimeoutMs { get; set; }
    public int? Seed { get; }

    public SimulatedCameraDriver(int? seed = 12345)
    {
        Seed = seed;
        _random = seed.HasValue ? new Random(seed.Value) : Random.Shared;
    }

    public void InjectDisconnect() => _disconnectNext = true;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        State = CameraConnectionState.Connecting;
        await Task.Delay(250, ct);
        Info = new CameraInfo("RoboCapture", "SimCam", "SIM-0001",
            CameraCapabilities.Capture | CameraCapabilities.Download | CameraCapabilities.Autofocus |
            CameraCapabilities.BatteryStatus | CameraCapabilities.StorageStatus,
            CameraCompatibilityTier.Full);
        State = CameraConnectionState.Connected;
        Event?.Invoke(new(DateTimeOffset.UtcNow, DriverId, State, "connect", "success"));
    }

    public Task DisconnectAsync(CancellationToken ct = default)
    {
        State = CameraConnectionState.Disconnected;
        Info = null;
        Event?.Invoke(new(DateTimeOffset.UtcNow, DriverId, State, "disconnect", "success"));
        return Task.CompletedTask;
    }

    public async Task<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken ct = default)
    {
        if (State != CameraConnectionState.Connected)
            return Failed("Camera is not connected.");

        if (_disconnectNext)
        {
            _disconnectNext = false;
            State = CameraConnectionState.Faulted;
            Info = null;
            Event?.Invoke(new(DateTimeOffset.UtcNow, DriverId, State, "capture", "disconnect", Error: "Simulated disconnect."));
            return Failed("Simulated disconnect.");
        }

        var captureStarted = DateTimeOffset.UtcNow;
        try
        {
            await DelayWithTimeoutAsync(CaptureLatencyMs, CaptureTimeoutMs, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Failed("Capture timeout.", captureStarted);
        }
        if (_random.NextDouble() < FailureRate)
            return Failed("Simulated capture failure.", captureStarted);

        Directory.CreateDirectory(request.DestinationFolder);
        var n = Interlocked.Increment(ref _counter);
        var cameraName = $"SIM_{n:000000}.JPG";
        var safeSubject = string.Concat(request.SubjectId.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_'));
        var localName = $"{safeSubject}_{request.PoseId}_{request.ShotNumber:00}_{cameraName}";
        var path = Path.Combine(request.DestinationFolder, localName);
        var suffix = 1;
        while (File.Exists(path))
        {
            path = Path.Combine(request.DestinationFolder, $"{safeSubject}_{request.PoseId}_{request.ShotNumber:00}_{Path.GetFileNameWithoutExtension(cameraName)}_{suffix++}{Path.GetExtension(cameraName)}");
        }
        var transferStarted = DateTimeOffset.UtcNow;
        Event?.Invoke(new(DateTimeOffset.UtcNow, DriverId, State, "capture", "exposure_completed",
            DateTimeOffset.UtcNow - captureStarted));
        try
        {
            await DelayWithTimeoutAsync(TransferLatencyMs, TransferTimeoutMs, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new CaptureResult(false, cameraName, null, DateTimeOffset.UtcNow, "Transfer timeout.",
                CaptureLifecycleState.ExposureCompleted, DateTimeOffset.UtcNow - captureStarted,
                DateTimeOffset.UtcNow - transferStarted);
        }
        if (_random.NextDouble() < TransferFailureRate)
            return new CaptureResult(false, cameraName, null, DateTimeOffset.UtcNow, "Simulated transfer failure.",
                CaptureLifecycleState.ExposureCompleted, DateTimeOffset.UtcNow - captureStarted,
                DateTimeOffset.UtcNow - transferStarted);
        await File.WriteAllTextAsync(path,
            $"ROBOCAPTURE SIMULATED IMAGE\nSession={request.SessionId}\nSubject={request.SubjectId}\nPose={request.PoseId}\nShot={request.ShotNumber}\nCameraFile={cameraName}\nCaptured={DateTimeOffset.UtcNow:O}\n", ct);
        var result = new CaptureResult(true, cameraName, path, DateTimeOffset.UtcNow, null,
            CaptureLifecycleState.Committed, DateTimeOffset.UtcNow - captureStarted,
            DateTimeOffset.UtcNow - transferStarted);
        Event?.Invoke(new(DateTimeOffset.UtcNow, DriverId, State, "capture", "committed",
            result.CaptureDuration + result.TransferDuration));
        return result;
    }

    private static CaptureResult Failed(string error, DateTimeOffset? started = null) =>
        new CaptureResult(false, null, null, DateTimeOffset.UtcNow, error, CaptureLifecycleState.Failed,
            started.HasValue ? DateTimeOffset.UtcNow - started.Value : null);

    private static async Task DelayWithTimeoutAsync(int delayMs, int? timeoutMs, CancellationToken ct)
    {
        using var timeout = timeoutMs.HasValue ? CancellationTokenSource.CreateLinkedTokenSource(ct) : null;
        if (timeout is not null) timeout.CancelAfter(timeoutMs.GetValueOrDefault());
        await Task.Delay(delayMs, timeout?.Token ?? ct);
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();
}
