namespace RoboCapture.Core;

public sealed record PoseStep(string PoseId, string Instruction, TimeSpan DelayBeforeCapture, int Shots, TimeSpan ShotInterval);
public sealed record PoseProgram(string Name, IReadOnlyList<PoseStep> Steps);
public sealed record PoseRunResult(string SessionId, int Requested, int Successful, IReadOnlyList<CaptureResult> Captures)
{
    public bool Success => Requested == Successful;
}

public interface ICaptureRecorder
{
    Task StartSessionAsync(string sessionId, string subjectId, CancellationToken ct = default);
    Task RecordCaptureAsync(string sessionId, CaptureRequest request, CaptureResult result, CancellationToken ct = default);
    Task CompleteSessionAsync(string sessionId, CancellationToken ct = default);
}

public sealed class PoseEngine(ICameraDriver camera, ICaptureRecorder? recorder = null)
{
    public event Action<string>? Status;

    public async Task<PoseRunResult> RunAsync(PoseProgram program, string subjectId, string destinationFolder, CancellationToken ct = default)
    {
        if (camera.State != CameraConnectionState.Connected)
            throw new InvalidOperationException("Camera must be connected before running a pose program.");

        var sessionId = Guid.NewGuid().ToString("N");
        var captures = new List<CaptureResult>();
        var requested = 0;
        var successful = 0;
        if (recorder is not null)
            await recorder.StartSessionAsync(sessionId, subjectId, ct);

        foreach (var step in program.Steps)
        {
            Status?.Invoke($"POSE {step.PoseId}: {step.Instruction}");
            await Task.Delay(step.DelayBeforeCapture, ct);

            for (var shot = 1; shot <= step.Shots; shot++)
            {
                requested++;
                Status?.Invoke($"CAPTURE {step.PoseId} shot {shot}/{step.Shots}");
                var request = new CaptureRequest(sessionId, subjectId, step.PoseId, shot, destinationFolder);
                CaptureResult result;
                try
                {
                    result = await camera.CaptureAsync(request, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    result = new CaptureResult(false, null, null, DateTimeOffset.UtcNow,
                        "Capture canceled.", CaptureLifecycleState.Failed);
                    if (recorder is not null)
                        await recorder.RecordCaptureAsync(sessionId, request, result, CancellationToken.None);
                    throw;
                }
                if (recorder is not null)
                    await recorder.RecordCaptureAsync(sessionId, request, result, ct);
                captures.Add(result);
                if (result.Success) successful++;
                Status?.Invoke(result.Success ? $"OK: {result.LocalPath}" : $"ERROR: {result.Error}");

                if (shot < step.Shots)
                    await Task.Delay(step.ShotInterval, ct);
            }
        }

        if (recorder is not null)
            await recorder.CompleteSessionAsync(sessionId, ct);
        return new PoseRunResult(sessionId, requested, successful, captures);
    }
}
