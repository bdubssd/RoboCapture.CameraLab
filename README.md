# RoboCapture Camera Lab 0.2

Testable offline vertical slice with a capability-driven camera contract, deterministic simulator, local SQLite persistence, and stress accounting. No manufacturer SDK is included.

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

## Why simulator first?
The same `ICameraDriver` contract will be implemented by Canon, Nikon, Sony and Panasonic adapters. This lets the pose engine, roster, event database, kiosk UI and networking remain manufacturer-independent.

## Next milestone
1. Add SQLite capture/event logging.
2. Add retry/recovery state machine.
3. Add a real manufacturer adapter without changing `PoseEngine`.
4. Run 1,000-shot reliability test.

## Git handoff
See [docs/GIT_WORKFLOW.md](docs/GIT_WORKFLOW.md) to enable automatic pushes for significant commits on each computer.
