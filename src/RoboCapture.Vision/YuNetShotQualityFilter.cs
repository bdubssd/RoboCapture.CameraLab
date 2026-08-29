using OpenCvSharp;

namespace RoboCapture.Vision;

/// <summary>
/// Second-generation <see cref="IShotQualityFilter"/>: YuNet, OpenCV's own modern face
/// detector (opencv/opencv_zoo, Apache-2.0, vendored as a ~230KB ONNX file under Models/ —
/// no download, fully offline, ships with the assembly). Meaningfully more accurate than the
/// Haar-cascade approach in <see cref="OpenCvShotQualityFilter"/> — trained on real faces
/// rather than hand-built feature templates — and returns 5-point landmarks (eyes, nose,
/// mouth corners) directly instead of a second cascade scanning sub-regions for eyes/smile.
/// </summary>
public sealed class YuNetShotQualityFilter : IShotQualityFilter, IDisposable
{
    private readonly string _modelPath;
    private readonly float _scoreThreshold;
    private FaceDetectorYN? _detector;
    private Size _detectorInputSize;

    public YuNetShotQualityFilter(string? modelPath = null, float scoreThreshold = 0.7f)
    {
        _modelPath = modelPath ?? Path.Combine(AppContext.BaseDirectory, "Models", "face_detection_yunet_2023mar.onnx");
        if (!File.Exists(_modelPath))
            throw new FileNotFoundException(
                $"YuNet model not found at '{_modelPath}'. It should ship alongside RoboCapture.Vision.dll " +
                "(Models/face_detection_yunet_2023mar.onnx, CopyToOutputDirectory).", _modelPath);
        _scoreThreshold = scoreThreshold;
    }

    public ShotScore Score(byte[] jpegBytes)
    {
        using var image = Cv2.ImDecode(jpegBytes, ImreadModes.Color);
        if (image.Empty())
            return new ShotScore(false, false, false, false, "Could not decode image bytes.");

        // This OpenCvSharp4 build's FaceDetectorYN takes its input size only at construction
        // (no SetInputSize). Cheap enough to recreate on a resolution change — captures from one
        // camera/mode are the same size call after call, so this only fires when that changes.
        var size = new Size(image.Width, image.Height);
        if (_detector is null || _detectorInputSize != size)
        {
            _detector?.Dispose();
            _detector = FaceDetectorYN.Create(_modelPath, string.Empty, size,
                scoreThreshold: _scoreThreshold, nmsThreshold: 0.3f, topK: 5000);
            _detectorInputSize = size;
        }

        using var faces = new Mat();
        _detector.Detect(image, faces);
        if (faces.Empty() || faces.Rows == 0)
            return new ShotScore(false, false, false, false, "No face detected.");

        // Each row: x, y, w, h, then 5 landmark (x,y) pairs, then a detection score.
        // Pick the largest box as the primary subject when more than one face is in frame.
        var bestRow = 0;
        var bestArea = -1.0;
        for (var row = 0; row < faces.Rows; row++)
        {
            var w = faces.At<float>(row, 2);
            var h = faces.At<float>(row, 3);
            var area = (double)w * h;
            if (area > bestArea) { bestArea = area; bestRow = row; }
        }

        var faceW = faces.At<float>(bestRow, 2);
        var faceH = faces.At<float>(bestRow, 3);
        var rightEye = new Point2f(faces.At<float>(bestRow, 4), faces.At<float>(bestRow, 5));
        var leftEye = new Point2f(faces.At<float>(bestRow, 6), faces.At<float>(bestRow, 7));
        var rightMouth = new Point2f(faces.At<float>(bestRow, 10), faces.At<float>(bestRow, 11));
        var leftMouth = new Point2f(faces.At<float>(bestRow, 12), faces.At<float>(bestRow, 13));

        using var gray = new Mat();
        Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY);

        var eyesOpen = IsEyeOpen(gray, rightEye, faceW) && IsEyeOpen(gray, leftEye, faceW);
        var smileDetected = IsSmiling(rightMouth, leftMouth, faceW);

        var reason = eyesOpen
            ? "Face and open eyes detected."
            : "Eye region too uniform/dark — likely blinking, occluded, or off-angle.";
        return new ShotScore(true, eyesOpen, smileDetected, Pass: eyesOpen, reason);
    }

    /// <summary>
    /// Heuristic, not a calibrated blink detector: crops a small patch around the eye landmark
    /// point and looks at local contrast (std-dev of pixel intensity). An open eye has a sharp
    /// iris/sclera/eyelid-crease boundary in a small crop; a closed eye is a much flatter patch
    /// of skin. Threshold is a starting point — needs tuning against real sample photos (see
    /// docs/AUTONOMOUS_CAPTURE_PLAN.md) once some exist.
    /// </summary>
    private static bool IsEyeOpen(Mat gray, Point2f eyeCenter, float faceWidth)
    {
        var half = Math.Max(4, (int)(faceWidth * 0.06));
        var x = Math.Clamp((int)eyeCenter.X - half, 0, gray.Width - 1);
        var y = Math.Clamp((int)eyeCenter.Y - half, 0, gray.Height - 1);
        var w = Math.Clamp(half * 2, 1, gray.Width - x);
        var h = Math.Clamp(half * 2, 1, gray.Height - y);
        using var patch = new Mat(gray, new Rect(x, y, w, h));
        Cv2.MeanStdDev(patch, out _, out var stdDev);
        return stdDev.Val0 > 12.0; // flat/low-contrast patch => probably closed
    }

    /// <summary>
    /// Heuristic: mouth-corner separation relative to face width. A smile widens the mouth;
    /// this is a coarse proxy, not a real expression classifier. Needs tuning against real
    /// samples, same caveat as <see cref="IsEyeOpen"/>.
    /// </summary>
    private static bool IsSmiling(Point2f rightMouth, Point2f leftMouth, float faceWidth)
    {
        var mouthWidth = Math.Abs(leftMouth.X - rightMouth.X);
        return faceWidth > 0 && mouthWidth / faceWidth > 0.42;
    }

    public void Dispose() => _detector?.Dispose();
}
