# Autonomous capture — plan draft

## Where this fits

Everything built so far (camera drivers, live view, save folder/format, roster
CSV, pose scheduling, capture DB/recovery) is the **precursor layer**: it lets
a human or a script tell the camera "take this shot, for this subject, now."
None of it looks at the image and decides whether the shot was *good*.

This doc is the plan for the layer that does that — the actual "unmanned
photographer" behavior: fire only on a good frame (eyes open, smiling,
correctly posed), and identify the subject without a person operating a
scanner.

Nothing below is built yet. This is a plan to review/adjust, not a status
report.

## Goal, restated

1. **Subject identification with little/no human operation** — either a
   physical QR/barcode scanner (works today, see `docs/TESTING.md`) or the
   camera's own live-view feed reading a QR code held up by/near the subject.
2. **Shot-quality gating** — don't commit (or don't even trigger) a capture
   unless the live-view frame passes: eyes open (no blink), a smile (or
   whatever the pose calls for), and reasonable head pose/framing.
3. All of this with the operator mostly stepping back — reviewing exceptions,
   not judging every frame.

## Two ways to feed frames to the quality check

- **Live-view frames** (already flowing at ~19fps from the Z6III driver) —
  cheap, fast, good enough resolution to judge eyes/smile/framing. This is
  the right source for the *gate* decision (when to trigger the shutter).
- **The captured JPEG itself** — full resolution, but only available *after*
  the shutter already fired. Useful as a final "was the delivered shot good
  enough to keep, or should we auto-retry this pose" check, not as the
  trigger signal.

Plan: use live view for the gating decision, and re-check the delivered JPEG
as a backstop/retry trigger.

## Options for the actual vision model

| Approach | What it buys | Cost |
|---|---|---|
| **ONNX face-analysis model run locally via `Microsoft.ML.OnnxRuntime`** | No cloud dependency, no per-shot cost, works at a school/studio with no internet. A small face-landmark/blendshape model (e.g. MediaPipe FaceMesh exported to ONNX, or a blink/smile-specific classifier) gives eye-aspect-ratio (blink) and mouth-curvature (smile) signals directly from landmarks. | Have to find/convert/vendor a model file, and validate it against real faces under studio lighting. This is the realistic default for a studio that must work offline. |
| **Cloud vision API (Azure Face, AWS Rekognition, Google Vision)** | Very accurate smile/eyes-open/pose attributes out of the box, near-zero integration work. | Needs internet + an account + per-image cost, and sends subject photos to a third party — likely a non-starter for a school photography context (student privacy) unless explicitly approved. |
| **OpenCV (via `OpenCvSharp4`) Haar/DNN face + eye cascades** | Very mature, well-documented, free, offline. Cruder than a landmark model (eye "open/closed" via cascade presence is noisy) but enough for a first cut and easy to prototype fast. | Less accurate blink/smile detection than a landmark model; would likely need tuning per-camera/lighting. |

**Recommendation:** start with OpenCvSharp4 for face detection + a simple
eye-region open/closed heuristic to get the pipeline (live-view → detect →
gate → trigger) working and testable end-to-end, then swap in an ONNX
landmark model for the real blink/smile accuracy once the plumbing is
proven. Skip cloud vision unless a later requirement makes offline
unworkable — sending student photos to a third-party API needs its own
explicit sign-off given who the subjects are.

## QR-from-camera vs QR-from-scanner

The wedge-scanner path (`SubjectIdentifier.Resolve`, wired to the scan
textbox) already works and needs no vision code — it's the lowest-risk path
to "little oversight" and should stay the default recommendation for
production. Camera-based QR reading is a nice-to-have for "hands-free," not
a requirement — `ZXing.NET` can decode a `System.Drawing`/`WriteableBitmap`
frame from live view directly if we want it later; small, well-isolated
addition whenever it's prioritized.

## Rough sequencing

1. **Prototype the gate loop against recorded frames, not live hardware
   first** — capture a folder of sample live-view frames (blinking,
   smiling, neutral, off-angle) from the Z6III we already have working,
   and get face-detect + eye/smile heuristics scoring them offline. This
   avoids burning camera/studio time on model tuning.
2. **Define the trigger contract**: a `IShotQualityGate` (or similar)
   interface — `bool IsGoodFrame(byte[] liveViewJpeg, out string reason)` —
   that `MainWindow`/`PoseEngine` can poll during the live-view stream and
   only fire `CaptureAsync` when it returns true (with a timeout/fallback
   so a subject who won't cooperate doesn't hang the session forever).
3. **Wire into `PoseEngine`**: extend `PoseStep` with an optional
   "wait for good frame, up to N seconds, else capture anyway and flag it"
   policy per pose, instead of the current fixed delay-then-shoot.
4. **Retry-on-bad-delivered-shot**: after a capture, run the same gate
   against the delivered JPEG; if it fails, auto-queue one retry shot for
   that pose before moving on, and log which shots were auto-retried for
   operator review.
5. **QR-from-camera** (optional, later): swap/augment the scan textbox
   with a background decode of live-view frames via `ZXing.NET`.
6. Validate end-to-end against the real Z6III, in real studio lighting,
   with real subjects — this is the step that will actually reveal whether
   the chosen model is good enough, and will likely drive threshold tuning.

## Open questions for you

- Is cloud vision (Azure/AWS/Google) genuinely off the table given student
  subjects, or only a concern for certain shoot types? Confirms whether the
  OpenCV/ONNX-only path is a hard requirement or just the current default.
- How much operator patience is there for a subject who won't produce a
  "good" frame — auto-capture after N seconds regardless, or hold and flag
  for the operator?
- Priority order: is blink/smile gating or hands-free QR-from-camera the
  more valuable one to build first? (Recommendation above assumes gating
  first, since it's the core "unmanned" behavior; QR-from-camera is a
  convenience on top of a scanner path that already works.)
