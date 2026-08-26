using System.IO;
using System.Windows;
using RoboCapture.Core;

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

        var application = new Application();
        application.Run(new MainWindow());
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
