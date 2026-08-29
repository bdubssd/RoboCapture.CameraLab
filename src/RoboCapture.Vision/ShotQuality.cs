namespace RoboCapture.Vision;

/// <summary>
/// Result of scoring one delivered capture. Deliberately granular (separate signals rather
/// than a single bool) so a caller can apply a pose-specific policy — e.g. some poses may not
/// require a smile — instead of this class making that call for every use site.
/// </summary>
/// <param name="FaceDetected">Whether a face was found in the frame at all.</param>
/// <param name="EyesOpen">
/// Heuristic only: true when the eye cascade found at least two eyes inside the face region.
/// Closed eyes are much harder for a Haar cascade to match than open ones, so "eyes found" is a
/// reasonable "probably open" signal and "eyes not found" is a reasonable "possibly blinking, or
/// occluded/off-angle" signal — but this is not a calibrated blink detector. See
/// docs/AUTONOMOUS_CAPTURE_PLAN.md for the planned upgrade to an ONNX landmark model.
/// </param>
/// <param name="SmileDetected">Heuristic only, same caveats as <see cref="EyesOpen"/>.</param>
/// <param name="Pass">
/// This filter's own opinion of "keep this shot without a human looking at it" — face found and
/// eyes open. Does not factor in <see cref="SmileDetected"/>; whether a pose requires a smile is
/// a per-pose policy decision for the caller, not this filter's to make.
/// </param>
/// <param name="Reason">Human-readable explanation, always populated, useful for logs/UI.</param>
public sealed record ShotScore(bool FaceDetected, bool EyesOpen, bool SmileDetected, bool Pass, string Reason);

/// <summary>
/// Scores a single delivered capture (JPEG bytes) for "was this shot usable." Implementations
/// must be self-contained and offline — no network calls — per the studio's offline requirement.
/// A pure function of image bytes: no camera dependency, so it's testable against a fixture
/// folder of sample images without any hardware attached.
/// </summary>
public interface IShotQualityFilter
{
    ShotScore Score(byte[] jpegBytes);
}
