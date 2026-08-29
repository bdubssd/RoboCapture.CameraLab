using System.Runtime.InteropServices;
using DlibDotNet;
using OpenCvSharp;

namespace RoboCapture.Vision;

/// <summary>
/// Blink/eyes-open detection via dlib's pretrained 68-point facial landmark model
/// (davisking/dlib-models, free for any use, vendored under Models/ — no download, fully
/// offline) plus the published Eye Aspect Ratio method (Soukupová &amp; Čech, "Real-Time Eye
/// Blink Detection using Facial Landmarks", 2016). This is the standard technique production
/// blink detectors use — a real published formula over a real pretrained landmark model, not a
/// heuristic invented for this project (unlike the contrast-based guess it replaces in
/// <see cref="YuNetShotQualityFilter"/>).
///
/// Needs an approximate face rectangle from a separate detector (YuNet's, here) — dlib's own
/// face detector is not used, avoiding a second, less accurate detection pass.
/// </summary>
public sealed class DlibEyeStateClassifier : IDisposable
{
    // EAR "open" threshold: a widely used starting point across many published implementations
    // of this method (commonly cited in the 0.2-0.25 range). Still unvalidated against this
    // studio's lighting/camera/angle — see docs/AUTONOMOUS_CAPTURE_PLAN.md.
    private const double EyeAspectRatioOpenThreshold = 0.21;

    private static readonly int[] RightEyeIndices = [36, 37, 38, 39, 40, 41];
    private static readonly int[] LeftEyeIndices = [42, 43, 44, 45, 46, 47];

    private readonly ShapePredictor _predictor;

    public DlibEyeStateClassifier(string? modelPath = null)
    {
        var path = modelPath ?? Path.Combine(AppContext.BaseDirectory, "Models", "shape_predictor_68_face_landmarks.dat");
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"dlib 68-point landmark model not found at '{path}'. It should ship alongside " +
                "RoboCapture.Vision.dll (Models/shape_predictor_68_face_landmarks.dat, CopyToOutputDirectory).", path);
        _predictor = ShapePredictor.Deserialize(path);
    }

    /// <summary>
    /// True when both eyes' Eye Aspect Ratio is above <see cref="EyeAspectRatioOpenThreshold"/>.
    /// </summary>
    /// <param name="bgrImage">The full image (BGR, as decoded by OpenCV).</param>
    /// <param name="faceRect">An approximate face bounding box in that image's pixel coordinates.</param>
    public bool AreEyesOpen(Mat bgrImage, Rect faceRect)
    {
        var rows = (uint)bgrImage.Rows;
        var cols = (uint)bgrImage.Cols;
        var step = (uint)bgrImage.Step();
        var buffer = new byte[step * rows];
        Marshal.Copy(bgrImage.Data, buffer, 0, buffer.Length);

        using var dlibImage = Dlib.LoadImageData<BgrPixel>(buffer, rows, cols, step);
        var rectangle = new Rectangle(faceRect.Left, faceRect.Top, faceRect.Right, faceRect.Bottom);
        using var shape = _predictor.Detect(dlibImage, rectangle);

        var rightEar = EyeAspectRatio(shape, RightEyeIndices);
        var leftEar = EyeAspectRatio(shape, LeftEyeIndices);
        return rightEar > EyeAspectRatioOpenThreshold && leftEar > EyeAspectRatioOpenThreshold;
    }

    /// <summary>
    /// (vertical1 + vertical2) / (2 * horizontal) over the eye's 6 landmark points, per
    /// Soukupová &amp; Čech. <paramref name="indices"/> must be given in that paper's point
    /// order: [p1 outer-corner, p2 upper-outer-lid, p3 upper-inner-lid, p4 inner-corner,
    /// p5 lower-inner-lid, p6 lower-outer-lid] — dlib's 68-point scheme already lays the eye
    /// points out in exactly this order.
    /// </summary>
    private static double EyeAspectRatio(FullObjectDetection shape, int[] indices)
    {
        var points = indices.Select(i => shape.GetPart((uint)i)).ToArray();
        var vertical1 = Distance(points[1], points[5]);
        var vertical2 = Distance(points[2], points[4]);
        var horizontal = Distance(points[0], points[3]);
        return horizontal > 0 ? (vertical1 + vertical2) / (2.0 * horizontal) : 0;
    }

    private static double Distance(DlibDotNet.Point a, DlibDotNet.Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt((double)dx * dx + (double)dy * dy);
    }

    public void Dispose() => _predictor.Dispose();
}
