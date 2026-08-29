using OpenCvSharp;

namespace RoboCapture.Vision;

/// <summary>
/// First-pass <see cref="IShotQualityFilter"/> implementation: stock OpenCV Haar cascades for
/// face, eyes, and smile (vendored in Cascades/, shipped with the assembly — no download, no
/// network access, works fully offline). Deliberately the "get the pipeline working" option per
/// docs/AUTONOMOUS_CAPTURE_PLAN.md — cascades are cruder than a landmark model, especially for
/// blink detection, and are expected to need per-lighting-setup tuning. Swap for an ONNX
/// landmark-based filter later without changing <see cref="IShotQualityFilter"/> callers.
/// </summary>
public sealed class OpenCvShotQualityFilter : IShotQualityFilter, IDisposable
{
    private readonly CascadeClassifier _faceCascade;
    private readonly CascadeClassifier _eyeCascade;
    private readonly CascadeClassifier _smileCascade;

    public OpenCvShotQualityFilter(string? cascadeDirectory = null)
    {
        var directory = cascadeDirectory ?? Path.Combine(AppContext.BaseDirectory, "Cascades");
        _faceCascade = LoadCascade(directory, "haarcascade_frontalface_default.xml");
        _eyeCascade = LoadCascade(directory, "haarcascade_eye.xml");
        _smileCascade = LoadCascade(directory, "haarcascade_smile.xml");
    }

    private static CascadeClassifier LoadCascade(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Haar cascade '{fileName}' not found at '{path}'. It should ship alongside " +
                "RoboCapture.Vision.dll (Cascades/*.xml, CopyToOutputDirectory).", path);
        var classifier = new CascadeClassifier();
        if (!classifier.Load(path))
            throw new InvalidOperationException($"OpenCV failed to load cascade '{path}'.");
        return classifier;
    }

    public ShotScore Score(byte[] jpegBytes)
    {
        using var image = Cv2.ImDecode(jpegBytes, ImreadModes.Color);
        if (image.Empty())
            return new ShotScore(false, false, false, false, "Could not decode image bytes.");

        using var gray = new Mat();
        Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.EqualizeHist(gray, gray); // improves cascade detection under uneven studio lighting

        var minFaceSize = new Size(gray.Width / 8, gray.Height / 8);
        var faces = _faceCascade.DetectMultiScale(gray, scaleFactor: 1.1, minNeighbors: 5,
            flags: HaarDetectionTypes.ScaleImage, minSize: minFaceSize);
        if (faces.Length == 0)
            return new ShotScore(false, false, false, false, "No face detected.");

        // If more than one face is in frame, judge the largest — assumed to be the subject
        // closest to camera, which is the one being posed/photographed.
        var face = faces.OrderByDescending(r => (long)r.Width * r.Height).First();

        // Eyes sit in roughly the upper 60% of a face box; restricting the search region cuts
        // down false positives from the eye cascade matching mouth/nostril texture lower down.
        var eyeRegionHeight = (int)(face.Height * 0.6);
        using var eyeRegion = new Mat(gray, new Rect(face.X, face.Y, face.Width, eyeRegionHeight));
        var minEyeSize = new Size(face.Width / 8, face.Width / 8);
        var eyes = _eyeCascade.DetectMultiScale(eyeRegion, scaleFactor: 1.1, minNeighbors: 4,
            flags: HaarDetectionTypes.ScaleImage, minSize: minEyeSize);

        // Closed eyes present far less of the contrast pattern the cascade was trained on than
        // open eyes, so "found at least two eyes" is a reasonable (not calibrated) open-eyes
        // signal. See the ShotScore.EyesOpen doc comment for the caveat.
        var eyesOpen = eyes.Length >= 2;

        // Smile sits in the lower half of the face box. The smile cascade is notoriously prone
        // to false positives (skin texture, shadows), so a much higher minNeighbors than face/eye
        // detection is used to require a stronger consensus before calling it a smile.
        var smileRegionY = face.Y + face.Height / 2;
        var smileRegionHeight = face.Height - face.Height / 2;
        using var smileRegion = new Mat(gray, new Rect(face.X, smileRegionY, face.Width, smileRegionHeight));
        var minSmileSize = new Size(face.Width / 4, face.Width / 4);
        var smiles = _smileCascade.DetectMultiScale(smileRegion, scaleFactor: 1.7, minNeighbors: 20,
            flags: HaarDetectionTypes.ScaleImage, minSize: minSmileSize);
        var smileDetected = smiles.Length > 0;

        var reason = eyesOpen
            ? "Face and open eyes detected."
            : eyes.Length == 1
                ? "Only one eye detected — possible blink, occlusion, or off-angle pose."
                : "No eyes detected — likely blinking, occluded, or facing away.";

        return new ShotScore(true, eyesOpen, smileDetected, Pass: eyesOpen, reason);
    }

    public void Dispose()
    {
        _faceCascade.Dispose();
        _eyeCascade.Dispose();
        _smileCascade.Dispose();
    }
}
