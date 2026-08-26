using RoboCapture.Core;
using RoboCapture.Persistence;
using Xunit;

namespace RoboCapture.Core.Tests;

public sealed class SimulatorTests
{
    [Fact]
    public async Task ThousandCapturesAreFullyAccountedFor()
    {
        await using var camera = new SimulatedCameraDriver(7) { CaptureLatencyMs = 0, TransferLatencyMs = 0 };
        await camera.ConnectAsync();
        var report = await new StressTestEngine(camera).RunAsync(
            new StressTestOptions(1000), Path.Combine(Path.GetTempPath(), "robocapture-stress-tests", Guid.NewGuid().ToString("N")));
        Assert.True(report.IsAccountedFor);
        Assert.Equal(1000, report.Attempts);
        Assert.Equal(1000, report.Successes);
        Assert.Equal(0, report.Failures);
        Assert.Contains("\"Attempts\": 1000", report.ToJson());
    }

    [Fact]
    public async Task TransferFailureDoesNotClaimALocalFile()
    {
        await using var camera = new SimulatedCameraDriver(7) { TransferFailureRate = 1, CaptureLatencyMs = 0, TransferLatencyMs = 0 };
        await camera.ConnectAsync();
        var folder = Path.Combine(Path.GetTempPath(), "robocapture-transfer-tests", Guid.NewGuid().ToString("N"));
        var result = await camera.CaptureAsync(new CaptureRequest("s", "subject", "pose", 1, folder));
        Assert.False(result.Success);
        Assert.Equal(CaptureLifecycleState.ExposureCompleted, result.State);
        Assert.Null(result.LocalPath);
    }

    [Fact]
    public async Task InjectedDisconnectCannotBecomeSuccess()
    {
        await using var camera = new SimulatedCameraDriver { CaptureLatencyMs = 0 };
        await camera.ConnectAsync();
        camera.InjectDisconnect();
        var result = await camera.CaptureAsync(new CaptureRequest("s", "subject", "pose", 1, Path.GetTempPath()));
        Assert.False(result.Success);
        Assert.Equal(CameraConnectionState.Faulted, camera.State);
        await camera.ConnectAsync();
        Assert.Equal(CameraConnectionState.Connected, camera.State);
    }

    [Fact]
    public async Task CancelledPoseSessionRemainsIncomplete()
    {
        var database = Path.Combine(Path.GetTempPath(), "robocapture-recovery-tests", Guid.NewGuid().ToString("N"), "capture.db");
        var store = new CaptureStore(database);
        await store.InitializeAsync();
        await using var camera = new SimulatedCameraDriver { CaptureLatencyMs = 100, TransferLatencyMs = 1 };
        await camera.ConnectAsync();
        using var cancellation = new CancellationTokenSource(20);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PoseEngine(camera, store).RunAsync(
            new PoseProgram("Recovery", new[] { new PoseStep("P1", "test", TimeSpan.Zero, 2, TimeSpan.Zero) }),
            "subject", Path.Combine(Path.GetTempPath(), "robocapture-recovery-tests"), cancellation.Token));
        Assert.NotEmpty(await store.GetIncompleteSessionIdsAsync());
        Assert.Equal(1, await store.GetCaptureCountAsync((await store.GetIncompleteSessionIdsAsync())[0]));
    }

    [Fact]
    public async Task CompletedSessionRecordsCaptureAndIsRecoverableAsComplete()
    {
        var database = Path.Combine(Path.GetTempPath(), "robocapture-complete-tests", Guid.NewGuid().ToString("N"), "capture.db");
        var store = new CaptureStore(database);
        await store.InitializeAsync();
        const string sessionId = "complete-session";
        await store.StartSessionAsync(sessionId, "subject");
        var request = new CaptureRequest(sessionId, "subject", "pose", 1, Path.GetTempPath());
        await store.RecordCaptureAsync(sessionId, request, new CaptureResult(true, "SIM_1.JPG", "local.jpg", DateTimeOffset.UtcNow, State: CaptureLifecycleState.Committed));
        await store.CompleteSessionAsync(sessionId);
        Assert.Equal(1, await store.GetCaptureCountAsync(sessionId));
        Assert.DoesNotContain(sessionId, await store.GetIncompleteSessionIdsAsync());
    }

    [Fact]
    public async Task CancellationStopsStressWithoutInventingUnaccountedResults()
    {
        await using var camera = new SimulatedCameraDriver { CaptureLatencyMs = 100, TransferLatencyMs = 1 };
        await camera.ConnectAsync();
        using var cancellation = new CancellationTokenSource(20);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new StressTestEngine(camera).RunAsync(
            new StressTestOptions(100), Path.Combine(Path.GetTempPath(), "robocapture-cancel-tests", Guid.NewGuid().ToString("N")), cancellation.Token));
    }

    [Fact]
    public async Task CameraEventsArePersistedToAuditLog()
    {
        var database = Path.Combine(Path.GetTempPath(), "robocapture-audit-tests", Guid.NewGuid().ToString("N"), "capture.db");
        var store = new CaptureStore(database);
        await store.InitializeAsync();
        await store.RecordCameraEventAsync(new CameraEvent(DateTimeOffset.UtcNow, "simulator.v1",
            CameraConnectionState.Connected, "capture", "committed"));
        Assert.Equal(1, await store.GetAuditEventCountAsync());
    }

    [Fact]
    public async Task PoseEnginePreservesOrderedMultiShotNumbering()
    {
        await using var camera = new SimulatedCameraDriver(7) { CaptureLatencyMs = 0, TransferLatencyMs = 0 };
        await camera.ConnectAsync();
        var folder = Path.Combine(Path.GetTempPath(), "robocapture-pose-tests", Guid.NewGuid().ToString("N"));
        var result = await new PoseEngine(camera).RunAsync(new PoseProgram("Sequence", new[]
        {
            new PoseStep("P1", "first", TimeSpan.Zero, 2, TimeSpan.Zero),
            new PoseStep("P2", "second", TimeSpan.Zero, 1, TimeSpan.Zero)
        }), "subject", folder);
        Assert.True(result.Success);
        Assert.Equal(3, result.Requested);
        Assert.Equal(new[] { "SIM_000001.JPG", "SIM_000002.JPG", "SIM_000003.JPG" },
            result.Captures.Select(capture => capture.CameraFileName));
    }

    [Fact]
    public async Task TransferFailureIsPersistedAsExposureCompleted()
    {
        var database = Path.Combine(Path.GetTempPath(), "robocapture-transfer-ledger-tests", Guid.NewGuid().ToString("N"), "capture.db");
        var store = new CaptureStore(database);
        await store.InitializeAsync();
        await using var camera = new SimulatedCameraDriver(7) { TransferFailureRate = 1, CaptureLatencyMs = 0, TransferLatencyMs = 0 };
        await camera.ConnectAsync();
        var result = await new PoseEngine(camera, store).RunAsync(new PoseProgram("Transfer", new[]
        {
            new PoseStep("P1", "test", TimeSpan.Zero, 1, TimeSpan.Zero)
        }), "subject", Path.Combine(Path.GetTempPath(), "robocapture-transfer-ledger-tests"));
        Assert.False(result.Success);
        Assert.Null(result.Captures.Single().LocalPath);
        Assert.Equal(new[] { nameof(CaptureLifecycleState.ExposureCompleted) }, await store.GetCaptureStatesAsync(result.SessionId));
        Assert.DoesNotContain(result.SessionId, await store.GetIncompleteSessionIdsAsync());
    }

    [Fact]
    public async Task ReconnectAllowsCaptureAfterInjectedDisconnect()
    {
        await using var camera = new SimulatedCameraDriver(7) { CaptureLatencyMs = 0, TransferLatencyMs = 0 };
        await camera.ConnectAsync();
        camera.InjectDisconnect();
        var first = await camera.CaptureAsync(new CaptureRequest("s", "subject", "pose", 1, Path.GetTempPath()));
        Assert.False(first.Success);
        await camera.ConnectAsync();
        var second = await camera.CaptureAsync(new CaptureRequest("s", "subject", "pose", 2, Path.Combine(Path.GetTempPath(), "robocapture-reconnect")));
        Assert.True(second.Success);
    }

    [Fact]
    public async Task CaptureTimeoutIsReportedWithoutCreatingAFile()
    {
        await using var camera = new SimulatedCameraDriver { CaptureLatencyMs = 100, CaptureTimeoutMs = 1 };
        await camera.ConnectAsync();
        var result = await camera.CaptureAsync(new CaptureRequest("s", "subject", "pose", 1,
            Path.Combine(Path.GetTempPath(), "robocapture-timeout-tests", Guid.NewGuid().ToString("N"))));
        Assert.False(result.Success);
        Assert.Equal("Capture timeout.", result.Error);
        Assert.Null(result.LocalPath);
    }

    [Fact]
    public async Task TransferTimeoutPreservesExposureState()
    {
        await using var camera = new SimulatedCameraDriver { CaptureLatencyMs = 0, TransferLatencyMs = 100, TransferTimeoutMs = 1 };
        await camera.ConnectAsync();
        var result = await camera.CaptureAsync(new CaptureRequest("s", "subject", "pose", 1,
            Path.Combine(Path.GetTempPath(), "robocapture-transfer-timeout-tests", Guid.NewGuid().ToString("N"))));
        Assert.False(result.Success);
        Assert.Equal(CaptureLifecycleState.ExposureCompleted, result.State);
        Assert.Equal("Transfer timeout.", result.Error);
        Assert.Null(result.LocalPath);
    }

    [Fact]
    public async Task ReusedDestinationNeverOverwritesAnExistingImage()
    {
        var folder = Path.Combine(Path.GetTempPath(), "robocapture-collision-tests", Guid.NewGuid().ToString("N"));
        var request = new CaptureRequest("s", "subject", "pose", 1, folder);
        await using var firstCamera = new SimulatedCameraDriver(7) { CaptureLatencyMs = 0, TransferLatencyMs = 0 };
        await firstCamera.ConnectAsync();
        var first = await firstCamera.CaptureAsync(request);
        await using var secondCamera = new SimulatedCameraDriver(7) { CaptureLatencyMs = 0, TransferLatencyMs = 0 };
        await secondCamera.ConnectAsync();
        var second = await secondCamera.CaptureAsync(request);
        Assert.True(first.Success && second.Success);
        Assert.NotEqual(first.LocalPath, second.LocalPath);
        Assert.Equal(2, Directory.GetFiles(folder).Length);
    }

    [Fact]
    public void RosterImporterPreservesStandardAndCustomFields()
    {
        var roster = "StudentID,FirstName,LastName,Grade,Homeroom,Barcode,Team,House\n" +
                     "S-001,Alex,\"Smith, Jr.\",5,A1,BC001,Blue,North\n";
        var subject = Assert.Single(CsvRosterImporter.Parse(roster));
        Assert.Equal("S-001", subject.StudentId);
        Assert.Equal("Smith, Jr.", subject.LastName);
        Assert.Equal("North", subject.CustomFields["House"]);
    }

    [Fact]
    public void RosterImporterRejectsRowsWithoutStudentId()
    {
        Assert.Throws<FormatException>(() => CsvRosterImporter.Parse("StudentID,FirstName\n,Alex\n"));
    }

    [Fact]
    public void SubjectIdentifierResolvesQrAndBarcodeValuesOffline()
    {
        var subject = Assert.Single(CsvRosterImporter.Parse("StudentID,FirstName,Barcode\nS-001,Alex,QR-001\n"));
        var identifier = new SubjectIdentifier(new[] { subject });
        var qr = identifier.Resolve(" qr-001 ", SubjectScanType.QrCode);
        var barcode = identifier.Resolve("S-001", SubjectScanType.Barcode);
        Assert.True(qr.Found);
        Assert.Equal("S-001", qr.Subject!.StudentId);
        Assert.True(barcode.Found);
    }

    [Fact]
    public void SubjectIdentifierRejectsUnknownAndEmptyScans()
    {
        var identifier = new SubjectIdentifier(Array.Empty<SubjectRecord>());
        Assert.Equal("Scan value is empty.", identifier.Resolve("  ").Error);
        Assert.Equal("No matching subject.", identifier.Resolve("unknown").Error);
    }
}