namespace RoboCapture.Core;

public enum CameraConnectionState { Disconnected, Connecting, Connected, Faulted }
public enum CameraCompatibilityTier { Full, CaptureAndVerify, TriggerOnly }
public enum CaptureLifecycleState
{
    CaptureRequested,
    CameraAcknowledged,
    ExposureCompleted,
    TransferStarted,
    FileReceived,
    FileVerified,
    Committed,
    Failed
}

[Flags]
public enum CameraCapabilities
{
    None = 0,
    Capture = 1 << 0,
    Download = 1 << 1,
    LiveView = 1 << 2,
    RemoteExposure = 1 << 3,
    Autofocus = 1 << 4,
    BatteryStatus = 1 << 5,
    StorageStatus = 1 << 6,
    ExposureSettings = 1 << 7
}

public sealed record CameraInfo(string Manufacturer, string Model, string SerialNumber, CameraCapabilities Capabilities,
    CameraCompatibilityTier CompatibilityTier = CameraCompatibilityTier.CaptureAndVerify);
public sealed record CaptureRequest(string SessionId, string SubjectId, string PoseId, int ShotNumber, string DestinationFolder);
public sealed record CaptureResult(bool Success, string? CameraFileName, string? LocalPath, DateTimeOffset Timestamp,
    string? Error = null, CaptureLifecycleState State = CaptureLifecycleState.Failed,
    TimeSpan? CaptureDuration = null, TimeSpan? TransferDuration = null);
public sealed record CameraEvent(DateTimeOffset Timestamp, string DriverId, CameraConnectionState State,
    string Operation, string Result, TimeSpan? Duration = null, string? Error = null);

public interface ICameraDriver : IAsyncDisposable
{
    string DriverId { get; }
    CameraConnectionState State { get; }
    CameraInfo? Info { get; }
    event Action<CameraEvent>? Event;
    Task ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync(CancellationToken ct = default);
    Task<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken ct = default);
}
