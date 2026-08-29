using System.IO;
using System.Windows;
using RoboCapture.Core;
using RoboCapture.NikonAdapter;
using RoboCapture.Vision;

namespace RoboCapture.CameraLab;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var stressArg = args.FirstOrDefault(arg => arg.StartsWith("--stress=", StringComparison.OrdinalIgnoreCase));
        if (stressArg is not null)
        {
            RunStressAsync(args, stressArg).GetAwaiter().GetResult();
            return;
        }

        var nikonTestArg = args.FirstOrDefault(arg => arg.StartsWith("--nikon-test=", StringComparison.OrdinalIgnoreCase));
        if (nikonTestArg is not null)
        {
            RunNikonTestAsync(args, nikonTestArg).GetAwaiter().GetResult();
            return;
        }

        var nikonLiveViewArg = args.FirstOrDefault(arg => arg.StartsWith("--nikon-liveview=", StringComparison.OrdinalIgnoreCase));
        if (nikonLiveViewArg is not null)
        {
            RunNikonLiveViewTestAsync(args, nikonLiveViewArg).GetAwaiter().GetResult();
            return;
        }

        var visionTestArg = args.FirstOrDefault(arg => arg.StartsWith("--vision-test=", StringComparison.OrdinalIgnoreCase));
        if (visionTestArg is not null)
        {
            RunVisionTest(visionTestArg);
            return;
        }

        var application = new Application();
        application.Run(new MainWindow());
    }

    /// <summary>
    /// Offline calibration harness: scores every .jpg/.jpeg in a folder with
    /// <see cref="OpenCvShotQualityFilter"/> and prints the result for each — no camera needed.
    /// Run against a folder of sample deliveries (blinking, smiling, neutral, off-angle) to see
    /// how the current heuristics behave before wiring this into the live capture flow. See
    /// docs/AUTONOMOUS_CAPTURE_PLAN.md step 1.
    /// </summary>
    private static void RunVisionTest(string visionTestArg)
    {
        var folder = visionTestArg.Split('=', 2)[1];
        if (!Directory.Exists(folder))
        {
            Console.WriteLine($"ERROR: folder not found: {folder}");
            return;
        }

        using var haarFilter = new OpenCvShotQualityFilter();
        using var yunetFilter = new YuNetShotQualityFilter();
        var files = Directory.GetFiles(folder)
            .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        if (files.Count == 0)
        {
            Console.WriteLine($"No .jpg/.jpeg files found in {folder}");
            return;
        }

        var haarPassed = 0;
        var yunetPassed = 0;
        foreach (var file in files)
        {
            var bytes = File.ReadAllBytes(file);
            var haar = haarFilter.Score(bytes);
            var yunet = yunetFilter.Score(bytes);
            if (haar.Pass) haarPassed++;
            if (yunet.Pass) yunetPassed++;
            Console.WriteLine($"{Path.GetFileName(file)}");
            Console.WriteLine($"  HAAR : face={haar.FaceDetected} eyesOpen={haar.EyesOpen} smile={haar.SmileDetected} PASS={haar.Pass} - {haar.Reason}");
            Console.WriteLine($"  YUNET: face={yunet.FaceDetected} eyesOpen={yunet.EyesOpen} smile={yunet.SmileDetected} PASS={yunet.Pass} - {yunet.Reason}");
        }
        Console.WriteLine($"SUMMARY: HAAR {haarPassed}/{files.Count} passed, YUNET {yunetPassed}/{files.Count} passed");
    }

    private static async Task RunNikonTestAsync(string[] args, string nikonTestArg)
    {
        var moduleDirectory = nikonTestArg.Split('=', 2)[1];
        var moduleFileName = args.FirstOrDefault(a => a.StartsWith("--nikon-module=", StringComparison.OrdinalIgnoreCase))
            ?.Split('=', 2)[1] ?? "Type0022.md3";
        var shotCount = int.TryParse(args.FirstOrDefault(a => a.StartsWith("--nikon-count=", StringComparison.OrdinalIgnoreCase))
            ?.Split('=', 2)[1], out var parsedCount) ? parsedCount : 1;

        var forceLegacy = args.Any(a => a.Equals("--nikon-legacy", StringComparison.OrdinalIgnoreCase));
        await using ICameraDriver camera = forceLegacy || moduleFileName.EndsWith(".md3", StringComparison.OrdinalIgnoreCase)
            ? new NikonCameraDriver(moduleDirectory, moduleFileName)
            : new NikonRemoteSdkV2CameraDriver(moduleDirectory, moduleFileName);
        camera.Event += cameraEvent => Console.WriteLine($"EVENT {cameraEvent.Operation}: {cameraEvent.Result} {cameraEvent.Error}");

        Console.WriteLine("Connecting...");
        await camera.ConnectAsync();
        Console.WriteLine($"Connected: {camera.Info?.Manufacturer} {camera.Info?.Model} (serial {camera.Info?.SerialNumber})");

        var folder = Path.Combine(Environment.CurrentDirectory, "captures", "nikon-test");
        var successes = 0;
        for (var shot = 1; shot <= shotCount; shot++)
        {
            var request = new CaptureRequest("nikon-test", "TEST", "MANUAL", shot, folder);
            Console.WriteLine($"Capturing {shot}/{shotCount}...");
            var result = await camera.CaptureAsync(request);
            if (result.Success) successes++;
            Console.WriteLine(result.Success
                ? $"OK: {result.LocalPath} (capture={result.CaptureDuration?.TotalMilliseconds:0}ms transfer={result.TransferDuration?.TotalMilliseconds:0}ms)"
                : $"FAILED: {result.Error} (state={result.State})");
        }
        Console.WriteLine($"SUMMARY: {successes}/{shotCount} succeeded");

        await camera.DisconnectAsync();
    }

    private static async Task RunNikonLiveViewTestAsync(string[] args, string nikonLiveViewArg)
    {
        var moduleDirectory = nikonLiveViewArg.Split('=', 2)[1];
        var moduleFileName = args.FirstOrDefault(a => a.StartsWith("--nikon-module=", StringComparison.OrdinalIgnoreCase))
            ?.Split('=', 2)[1] ?? "ControlServiceLayer.dll";

        await using var camera = new NikonRemoteSdkV2CameraDriver(moduleDirectory, moduleFileName);
        camera.Event += cameraEvent => Console.WriteLine($"EVENT {cameraEvent.Operation}: {cameraEvent.Result} {cameraEvent.Error}");

        var frameCount = 0;
        var folder = Path.Combine(Environment.CurrentDirectory, "captures", "nikon-liveview");
        Directory.CreateDirectory(folder);
        camera.LiveViewFrame += frame =>
        {
            frameCount++;
            Console.WriteLine($"FRAME {frameCount}: {frame.Length} bytes, header={BitConverter.ToString(frame, 0, Math.Min(4, frame.Length))}");
            if (frameCount <= 3)
                File.WriteAllBytes(Path.Combine(folder, $"frame-{frameCount}.jpg"), frame);
        };

        Console.WriteLine("Connecting...");
        await camera.ConnectAsync();
        Console.WriteLine($"Connected: {camera.Info?.Manufacturer} {camera.Info?.Model}");

        Console.WriteLine("Starting live view...");
        await camera.StartLiveViewAsync();
        Console.WriteLine("Live view started, waiting 8s for frames...");
        await Task.Delay(8000);

        Console.WriteLine($"Total frames received: {frameCount}");
        await camera.StopLiveViewAsync();
        await camera.DisconnectAsync();
    }

    private static async Task RunStressAsync(string[] args, string stressArg)
    {
        var count = int.Parse(stressArg.Split('=', 2)[1]);
        var camera = new SimulatedCameraDriver
        {
            FailureRate = ParseRate(args, "--capture-failure="),
            TransferFailureRate = ParseRate(args, "--transfer-failure=")
        };
        await camera.ConnectAsync();

        var folder = Path.Combine(Environment.CurrentDirectory, "captures", "stress");
        var report = await new StressTestEngine(camera).RunAsync(new StressTestOptions(count), folder);
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "stress-report.json"), report.ToJson());

        Console.WriteLine($"attempts={report.Attempts} successes={report.Successes} failures={report.Failures} unaccounted={report.UnaccountedAttempts}");
        Console.WriteLine(report.IsAccountedFor ? "ACCOUNTED" : "UNACCOUNTED");
    }

    private static double ParseRate(string[] args, string prefix)
    {
        var arg = args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return arg is not null && double.TryParse(arg.Split('=', 2)[1], out var value) ? value : 0.0;
    }
}
