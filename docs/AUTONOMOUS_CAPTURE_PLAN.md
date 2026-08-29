# Autonomous capture — plan

**Decisions locked in (2026-08-28):**
1. **Must run fully offline.** No cloud vision APIs — rules out Azure/AWS/Google Face
   entirely, not just "prefer local." Every model in this plan runs on-device.
2. **Burst capture, not live-view gating-as-trigger.** Each pose fires a
   programmable number of shots at a programmable interval (this already exists
   as `PoseStep.Shots`/`ShotInterval` — needs UI + a quality pass wired on top,
   not a redesign). Quality filtering happens *after* the burst, against the
   delivered files, rather than trying to time the shutter off a live-view
   prediction.
3. **Both** blink/smile gating and QR-from-camera subject ID are required —
   no priority order between them; plan to build both.

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
2. **Shot-quality filtering** — for each pose, fire a programmable number of
   shots at a programmable interval (burst), then automatically judge each
   delivered frame (eyes open, smile present, reasonable head pose/framing)
   and flag or discard the bad ones instead of leaving that to a human
   reviewing every frame.
3. All of this with the operator mostly stepping back — reviewing exceptions
   (nothing in the burst passed), not judging every frame.

## Burst-then-filter, not live-trigger-gating

Given the "programmable shot count + interval" decision, the simplest and
most robust design is: **don't try to time the shutter off a live prediction
at all.** Fire the whole programmed burst for a pose on schedule (this is
already `PoseStep.Shots`/`ShotInterval`, executed by `PoseEngine`), then run
the quality check against each delivered JPEG afterward:

- Simpler to build and to reason about — no need to synchronize a live-view
  analysis loop with shutter timing, no risk of a subject "gaming" the timing
  window, no live-view-frame-vs-delivered-frame quality mismatch to worry
  about.
- Matches how a human photographer actually works a session — take several,
  keep the good one(s) — rather than trying to predict the one perfect
  instant.
- Live view still has a role, just a smaller one: an optional "subject
  detected and roughly framed" check before the burst starts (so the burst
  doesn't waste all its shots on an empty chair), not a per-frame smile/blink
  predictor.

## Vision model options (offline only — cloud vision is ruled out)

| Approach | What it buys | Cost |
|---|---|---|
| **OpenCV (via `OpenCvSharp4`) Haar/DNN face + eye cascades** | Very mature, well-documented, free, fully offline, no model-sourcing risk (ships with OpenCV). Cruder than a landmark model (eye "open/closed" via cascade presence is noisy) but enough for a first cut and fast to prototype. | Less accurate blink/smile detection than a landmark model; needs tuning per-camera/lighting. |
| **ONNX face-analysis model run locally via `Microsoft.ML.OnnxRuntime`** | No cloud dependency, no per-shot cost, fully offline. A small face-landmark/blendshape model (e.g. MediaPipe FaceMesh exported to ONNX, or a blink/smile-specific classifier) gives eye-aspect-ratio (blink) and mouth-curvature (smile) signals directly from landmarks — meaningfully more accurate than cascades. | Have to find/vendor/license a model file and validate it against real faces under your studio lighting. |

**Update (2026-08-28):** built both. `OpenCvShotQualityFilter` (Haar cascades)
was the first cut; testing it against a real camera JPEG with no person in
frame produced a confirmed false positive (`face=True` on an empty studio
shot). `YuNetShotQualityFilter` — OpenCV's own modern face detector
(opencv/opencv_zoo, Apache-2.0, ~230KB ONNX file, vendored under `Models/`,
fully offline) — was added on top and correctly reports no face on the same
image. YuNet also returns 5-point landmarks (eyes, nose, mouth corners)
directly, used for eyes-open (local contrast in a small crop around each eye
point) and smile (mouth-corner separation) heuristics — cruder proxies than
a full landmark model, but no second cascade scan needed. **YuNet is now the
recommended default**; `OpenCvShotQualityFilter` stays in the codebase as a
comparison baseline (the `--vision-test` CLI harness runs both side by side).
Cloud vision is off the table per the offline requirement, dropped from the
options entirely.

**Update (2026-08-28, later same day):** replaced both remaining heuristics
with pretrained models too — every signal `YuNetShotQualityFilter` reports is
now backed by a model or published method, not code we guessed:

- **Smile**: Microsoft's FER+ emotion classifier (official ONNX Model Zoo,
  `onnx/models`, MIT license) run via OpenCV's DNN module — a real 8-class
  emotion classifier trained on labeled faces, not a mouth-width heuristic.
- **Eyes-open**: dlib's pretrained 68-point facial landmark model
  (`davisking/dlib-models`, free for any use) plus the Eye Aspect Ratio
  method (Soukupová & Čech, 2016) — the standard published blink-detection
  technique, not a contrast-based guess.
- **Face**: YuNet, unchanged from the earlier update.

All three model files (~130MB total, mostly the dlib landmark model) are
vendored under `src/RoboCapture.Vision/Models/` and committed to the repo —
worth knowing given the size, but necessary for the app to run with zero
setup on an offline machine.

Still unvalidated: **no filter has been run against an actual face yet** —
every test so far is true-negative-only (confirms all three correctly say
"no face" on empty scenes, and the pipeline loads/runs without crashing).
Being pretrained means each signal is *individually* credible (validated by
its original authors on real datasets), not that the combination is
calibrated for this studio's camera, lighting, and angles — real sample
photos are still the next concrete step to see actual pass/fail behavior
and, if needed, tune the EAR and happiness-probability thresholds.

## QR-from-camera vs QR-from-scanner

The wedge-scanner path (`SubjectIdentifier.Resolve`, wired to the scan
textbox) already works today and needs no vision code. Camera-based QR
reading (via `ZXing.NET`, fully offline, decoding frames straight from the
live-view stream) is being built as a first-class feature alongside it, not
a someday nice-to-have — both are in the plan below. `ZXing.NET` has no
cloud dependency, so it fits the offline requirement cleanly.

## Rough sequencing

1. **Prototype the quality filter against recorded frames, not live hardware
   first** — capture a folder of sample delivered JPEGs (blinking, smiling,
   neutral, off-angle) from the Z6III we already have working, and get
   face-detect + eye/smile heuristics scoring them offline. Avoids burning
   camera/studio time on model tuning.
2. **Define the scoring contract**: an `IShotQualityFilter` (or similar)
   interface — `ShotScore Score(byte[] jpegBytes)` returning something like
   eyes-open/smile/framing booleans plus an overall pass/fail and a reason
   string — pure function over image bytes, no camera dependency, so it's
   unit-testable against a fixture folder of sample images.
3. **Wire the programmable burst into the UI**: `PoseStep.Shots`/
   `ShotInterval` already model "N shots, fixed interval" — MainWindow needs
   controls to set them per session (today `CAPTURE X N` + `Interval ms`
   already cover the simple case; formalizing this as a `PoseProgram` in the
   UI is the main gap).
4. **Wire the filter into `PoseEngine`/`MainWindow` after each burst**: score
   every delivered file for the pose, mark each as kept/flagged in the
   capture DB (`RoboCapture.Persistence`), and surface a simple "N of M kept"
   summary per pose rather than requiring a human to open every file.
5. **Optional live-view pre-check**: before starting a burst, a lightweight
   "is there a face roughly centered in frame" check (reuses the same
   OpenCV/ONNX face detector) so a burst doesn't fire at an empty chair.
6. **QR-from-camera**: background-decode live-view frames via `ZXing.NET`
   for subject ID, as an alternative/addition to the wedge-scanner path,
   both wired to the same `SubjectIdentifier.Resolve`.
7. Validate end-to-end against the real Z6III, in real studio lighting, with
   real subjects — this is the step that reveals whether the chosen model is
   good enough, and will likely drive threshold tuning.
