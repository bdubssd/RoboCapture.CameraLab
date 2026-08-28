using System.IO;
using System.Windows;
using RoboCapture.Core;
using RoboCapture.NikonAdapter;

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

        var application = new Application();
        application.Run(new MainWindow());
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
