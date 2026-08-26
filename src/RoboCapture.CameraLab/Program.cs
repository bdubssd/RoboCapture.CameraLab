using System.Windows;

namespace RoboCapture.CameraLab;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        var application = new Application();
        application.Run(new MainWindow());
    }
}
