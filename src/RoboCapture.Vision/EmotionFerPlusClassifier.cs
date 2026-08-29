using OpenCvSharp;
using OpenCvSharp.Dnn;

namespace RoboCapture.Vision;

/// <summary>
/// Wraps Microsoft's FER+ emotion classifier — a genuinely pretrained model (trained on the
/// FER2013+ labeled dataset, not a heuristic we invented), published in the official ONNX
/// Model Zoo (onnx/models, MIT license), vendored under Models/ and run fully offline via
/// OpenCV's own DNN module (no new native dependency beyond OpenCvSharp, which this project
/// already needs for face detection). Replaces the guessed mouth-width heuristic that
/// previously stood in for smile detection.
///
/// Model contract (from the ONNX Model Zoo model card): input "Input3" is a 1x1x64x64 grayscale
/// tensor of a roughly-centered face crop; output is 8 raw (pre-softmax) scores in the order
/// neutral, happiness, surprise, sadness, anger, disgust, fear, contempt.
/// </summary>
public sealed class EmotionFerPlusClassifier : IDisposable
{
    private const int EmotionCount = 8;
    private const int HappinessIndex = 1;
    private static readonly Size InputSize = new(64, 64);

    private readonly Net _net;

    public EmotionFerPlusClassifier(string? modelPath = null)
    {
        var path = modelPath ?? Path.Combine(AppContext.BaseDirectory, "Models", "emotion_ferplus.onnx");
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"FER+ emotion model not found at '{path}'. It should ship alongside " +
                "RoboCapture.Vision.dll (Models/emotion_ferplus.onnx, CopyToOutputDirectory).", path);
        _net = CvDnn.ReadNetFromOnnx(path)
            ?? throw new InvalidOperationException($"OpenCV failed to load FER+ model '{path}'.");
    }

    /// <summary>
    /// Runs the classifier on a grayscale crop of just the face (any size — resized internally).
    /// Returns the softmax-normalized probability of the "happiness" class.
    /// </summary>
    public double HappinessProbability(Mat grayFaceCrop)
    {
        using var resized = new Mat();
        Cv2.Resize(grayFaceCrop, resized, InputSize);
        using var blob = CvDnn.BlobFromImage(resized, scaleFactor: 1.0, size: InputSize,
            mean: default, swapRB: false, crop: false);
        _net.SetInput(blob);
        using var output = _net.Forward();

        var scores = new float[EmotionCount];
        for (var i = 0; i < EmotionCount; i++)
            scores[i] = output.At<float>(0, i);

        return Softmax(scores)[HappinessIndex];
    }

    private static double[] Softmax(float[] scores)
    {
        var max = scores.Max();
        var exp = scores.Select(s => Math.Exp(s - max)).ToArray();
        var sum = exp.Sum();
        return exp.Select(e => e / sum).ToArray();
    }

    public void Dispose() => _net.Dispose();
}
