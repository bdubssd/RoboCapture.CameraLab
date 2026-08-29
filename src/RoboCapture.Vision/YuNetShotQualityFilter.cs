using OpenCvSharp;

namespace RoboCapture.Vision;

/// <summary>
/// Second-generation <see cref="IShotQualityFilter"/>: YuNet for face detection
/// (opencv/opencv_zoo, Apache-2.0), Microsoft's FER+ classifier for smile detection (official
/// ONNX Model Zoo, MIT), and dlib's 68-point landmark model + the published Eye Aspect Ratio
/// method for eyes-open (davisking/dlib-models, free for any use). All three model files are
/// vendored under Models/ and run fully offline — no network calls, no cloud dependency. Every
/// signal here is backed by a model or method published/trained by someone other than this
/// project, not a from-scratch heuristic — see docs/AUTONOMOUS_CAPTURE_PLAN.md for the
/// reasoning and what's still unvalidated (none of these have been run against a real face yet).
/// </summary>
public sealed class YuNetShotQualityFilter : IShotQualityFilter, IDisposable
{
    private const double SmileProbabilityThreshold = 0.5;

    private readonly string _modelPath;
    private readonly float _scoreThreshold;
    private readonly EmotionFerPlusClassifier _emotionClassifier;
    private readonly DlibEyeStateClassifier _eyeStateClassifier;
    private FaceDetectorYN? _detector;
    private Size _detectorInputSize;

    public YuNetShotQualityFilter(string? modelPath = null, float scoreThreshold = 0.7f,
        string? emotionModelPath = null, string? landmarkModelPath = null)
    {
        _modelPath = modelPath ?? Path.Combine(AppContext.BaseDirectory, "Models", "face_detection_yunet_2023mar.onnx");
        if (!File.Exists(_modelPath))
            throw new FileNotFoundException(
                $"YuNet model not found at '{_modelPath}'. It should ship alongside RoboCapture.Vision.dll " +
                "(Models/face_detection_yunet_2023mar.onnx, CopyToOutputDirectory).", _modelPath);
        _scoreThreshold = scoreThreshold;
        _emotionClassifier = new EmotionFerPlusClassifier(emotionModelPath);
        _eyeStateClassifier = new DlibEyeStateClassifier(landmarkModelPath);
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

        var faceX = faces.At<float>(bestRow, 0);
        var faceY = faces.At<float>(bestRow, 1);
        var faceW = faces.At<float>(bestRow, 2);
        var faceH = faces.At<float>(bestRow, 3);

        var faceRect = new Rect(
            Math.Clamp((int)faceX, 0, image.Width - 1),
            Math.Clamp((int)faceY, 0, image.Height - 1),
            0, 0);
        faceRect.Width = Math.Clamp((int)faceW, 1, image.Width - faceRect.X);
        faceRect.Height = Math.Clamp((int)faceH, 1, image.Height - faceRect.Y);

        // dlib's shape predictor works directly on the color image + face rect; give it some
        // margin beyond YuNet's tight box since landmark models generally expect a slightly
        // looser crop than a pure detector box.
        var eyesOpen = _eyeStateClassifier.AreEyesOpen(image, faceRect);

        using var gray = new Mat();
        Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY);
        using var faceCrop = new Mat(gray, faceRect);
        var happiness = _emotionClassifier.HappinessProbability(faceCrop);
        var smileDetected = happiness >= SmileProbabilityThreshold;

        var reason = eyesOpen
            ? $"Face and open eyes detected (happiness={happiness:P0})."
            : "Eye Aspect Ratio below threshold — likely blinking, occluded, or off-angle.";
        return new ShotScore(true, eyesOpen, smileDetected, Pass: eyesOpen, reason);
    }

    public void Dispose()
    {
        _detector?.Dispose();
        _emotionClassifier.Dispose();
        _eyeStateClassifier.Dispose();
    }
}
