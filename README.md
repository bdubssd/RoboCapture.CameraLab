# RoboCapture Camera Lab 0.2

Testable offline vertical slice with a capability-driven camera contract, deterministic simulator, local SQLite persistence, stress accounting, Nikon camera adapters and an experimental shot-quality (blink/smile) filter. Manufacturer SDK binaries are not included in the repository; see [docs/NIKON_SDK_NOTES.md](docs/NIKON_SDK_NOTES.md).

## Requirements
- Windows 11 recommended
- .NET 8 SDK or Visual Studio 2022 with .NET desktop development

## Run
From a terminal in this folder:

    dotnet run --project src/RoboCapture.CameraLab

Open `RoboCapture.CameraLab.sln` in Visual Studio and run `RoboCapture.CameraLab`, then use `RUN STRESS TEST` with `Count` set to `1000`. The machine-readable report is written to `captures/stress/stress-report.json`.

## Expected result
The app opens a Camera Lab window and creates simulated capture files under:

    src/RoboCapture.CameraLab/captures/<student-id>/

Each file contains session, subject, pose, shot, camera filename, and timestamp metadata.

## Roster and recovery
Use `LOAD ROSTER` in the app to load a CSV roster (see `docs/TESTING.md` for accepted columns). The ImageWorks Production Registry (`iwreg roster export`) writes a roster in exactly this format with `StudentID` set to each athlete's permanent member ID, so scanning an athlete's QR card files their captures under `captures/<member ID>/`, ready to be ingested with identity attached. Once loaded, the QR/barcode scan field resolves scanned values against the roster instead of using the raw scan as the subject ID. If the app is closed mid-session (crash, power loss, cancel), incomplete sessions are listed on next launch with a `MARK REVIEWED` action.

## Why simulator first?
The same `ICameraDriver` contract will be implemented by Canon, Nikon, Sony and Panasonic adapters. This lets the pose engine, roster, event database, kiosk UI and networking remain manufacturer-independent.

## Next milestone
1. ~~Add SQLite capture/event logging.~~ Done — wired into the Camera Lab window.
2. ~~Add retry/recovery state machine.~~ Done — incomplete sessions from a crash or cancel surface on startup with a review/mark-reviewed action.
3. ~~Add a real manufacturer adapter without changing `PoseEngine`.~~ Done for Nikon — `src/RoboCapture.NikonAdapter` (legacy MAID3 + Remote SDK v2, Z-series live view). Hardware status per [docs/NIKON_SDK_NOTES.md](docs/NIKON_SDK_NOTES.md): Z6III live view and capture work, with a known limitation on repeated-capture download; D850 connects but capture-to-download is not yet reliable. Other brands: [docs/TETHERING_RESEARCH_2026-10-02.md](docs/TETHERING_RESEARCH_2026-10-02.md).
4. ~~Run 1,000-shot reliability test.~~ Done — `dotnet run --project src/RoboCapture.CameraLab -- --stress=1000` runs headless and writes `captures/stress/stress-report.json`.

## Git handoff
See [docs/GIT_WORKFLOW.md](docs/GIT_WORKFLOW.md) to enable automatic pushes for significant commits on each computer.
