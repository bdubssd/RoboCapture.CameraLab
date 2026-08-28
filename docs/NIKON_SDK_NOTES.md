# Nikon adapter — status and SDK notes

Two `ICameraDriver` implementations exist in `src/RoboCapture.NikonAdapter`:

- **`NikonCameraDriver`** — legacy per-model MAID 3.0 modules (e.g. `Type0022.md3` for the D850).
  Drives the raw `MAIDEntryPoint` Module/Source/Item/DataObj object graph directly.
- **`NikonRemoteSdkV2CameraDriver`** — the newer "Remote SDK v2" unified module
  (`ControlServiceLayer.dll`, covers current Z-series bodies). Uses the simplified
  `InitializeSDK`/`ConnectDevice`/`StartShooting` API.

Both are verified against real hardware (Nikon D850, Nikon Z6III) on this machine's connected
cameras, and both are wired into `MainWindow` via a Driver dropdown (Simulator / Nikon Legacy
MAID3 / Nikon Remote SDK v2) with module-folder browse + Switch Camera. `Program.cs` also has
CLI flags used for hardware verification during development: `--nikon-test=<dir>`
(`--nikon-module`, `--nikon-count`, `--nikon-legacy`) for capture, and `--nikon-liveview=<dir>`
for live view.

## Live view (Z-series, Remote SDK v2)

`NikonRemoteSdkV2CameraDriver` exposes `StartLiveViewAsync`/`StopLiveViewAsync` and a
`LiveViewFrame` event (raw JPEG bytes per frame). Verified on a Z6III: ~19fps, each frame a
consistent 708096-byte JPEG. Wired into `MainWindow` as LIVE VIEW ON/OFF buttons plus an `Image`
control.

The frame arrives wrapped in `NkMAIDLiveViewData` (`ulLvImageSize`, `wPhysicalBytes`,
`wLogicalBits`, an embedded `NKMAIDLiveViewHeader` struct, then `pImageData`). That header
carries ~30 fields of live-view telemetry (AF points, angles, face recognition, etc.) this
driver doesn't need, so rather than hand-porting the whole struct into C#, `Maid3V2Native.cs`
computes just the one offset that matters — `LiveViewImageDataOffset = 892` — by hand-counting
the header's field sizes under `#pragma pack(2)`. This was risky enough to want empirical
verification before trusting it: confirmed by checking every captured frame started with the
JPEG SOI/DQT marker bytes (`FF D8 FF DB`), which it did, 155/155 frames in the test run.

## Camera sleep drops the SDK connection

Nikon's auto power-off suspends USB communication entirely — `InitializeSDK` then enumerates
zero devices even though Windows still lists the camera's USB device with `Status: OK`. No
software-side retry can fix this; the camera needs a physical wake (half shutter-press or any
button). For unattended studio use, disable auto power-off in the camera's own menu for the
duration of the tethered session — this is standard practice for all tethering tools, not
specific to this driver.

## Getting the SDK

Vendor SDK binaries are not committed to this repo (proprietary, and large). Register at
[Nikon's SDK developer portal](https://sdk.nikonimaging.com/apply/) for the **Camera Remote
SDK** (not the Image SDK, which is only for RAW decoding), download the package(s) for your
camera model(s), and unzip into `vendor-sdks/nikon/` locally (gitignored).

- **Legacy DSLRs** ship a shared set of DLLs (`NkdPTP.dll`, `NkRoyalmile.dll`, `dnssd.dll`) plus
  a model-specific `.md3` file (e.g. `Type0022.md3` for the D850) — all four files must sit in
  the same folder.
- **Z-series** unified SDK ships `ControlServiceLayer.dll` plus the same three shared DLLs, and
  additionally requires three `.config` files (`DC_PTP_Config.config`, `MaidLayer.config`,
  `RangeValue.config`) to be copied to `%LocalAppData%\Nikon\NXTether` — **not** next to the
  DLL. This is documented in the SDK's own `ReadMe_En.txt` under "Usage notes" and is easy to
  miss.

Close Camera Control Pro 2, NX Tether, or Nikon Transfer 2 before connecting — the SDK's own
docs warn Remote SDK v2 "may not operate correctly" if any of those hold the camera.

## Known limitation: Z-series repeat-capture download

**Symptom:** `NikonRemoteSdkV2CameraDriver.CaptureAsync` reliably shoots and downloads on the
**first** capture after the camera powers on. Every subsequent capture — same session, fresh
reconnect, any `SaveMedia` value tried — fires the shutter and `StartShooting` reports success,
but no file ever appears at `ImageSavePath`.

### Diagnosis (confirmed on a Z6III)

- `SaveMedia` (`kNkMAIDCapability_SaveMedia`, `0x8305`) must be set to `2` (Card+SDRAM) for the
  camera to offer bytes to the host at all — default is card-only. Verified via `GetCapability`
  readback that the value is actually applied (`value=2`), every time, on every attempt.
- Registered `EventProc` and logged every native event fired. On every capture — successful or
  not — the module fires `kNkMAIDEvent_CaptureComplete` (`0x108`). Per the official
  `MAID3-COMMON(E).pdf` spec, this event's data parameter distinguishes the two halves of the
  completion: `0` = "the all SDRAM images are finished to read or deleted", `1` = "the all
  images are finished to record in card." **Only `data=1` (card) is ever observed after the
  first shot.** The SDRAM half of the event never fires again until the camera is power-cycled.
- Ruled out as causes: per-shot `SaveMedia` toggling (0→2 every shot, forcing a real
  transition), removing diagnostic `GetShootingStatus` polling during the wait (in case it
  interfered with the transfer), `SaveMedia=1` (SDRAM-only — camera refuses to shoot at all
  without a card as a valid destination), longer timeouts (file never appears even after 30s;
  when it works, transfer completes in <500ms).
- Tried combining the v2 API with the legacy MAID3 object graph (`ControlServiceLayer.dll`
  exports both `MAIDEntryPoint` and the v2 functions from the same session) on the theory that
  the legacy `Capture`/`Acquire` flow — which Nikon's D850 usage doc explicitly documents as
  the correct SDRAM-shooting path — might not have this bug. Opening the legacy Module object
  after `InitializeSDK`/`ConnectDevice` fails immediately (`kNkMAIDResult_UnexpectedError`,
  `-117`): the v2 session holds the underlying MAID3 session exclusively: the two APIs are not
  usable side-by-side against this DLL.

### What this means

This looks like a genuine gap in Nikon's Remote SDK v2 "Simplified API" wrapper — `StartShooting`
doesn't reliably route repeat shots to the SDRAM capture path the way it's documented to, on
this camera/SDK version combination. Third-party tethering tools (digiCamControl, Smart
Shooter) don't hit this because they don't use Nikon's SDK's simplified wrapper at all — they
implement the PTP vendor extension directly (see digiCamControl's
[`NikonBase.cs`](https://github.com/dukus/digiCamControl/blob/master/CameraControl.Devices/Nikon/NikonBase.cs),
which issues a distinct `InitiateCaptureRecInSdram` PTP command rather than relying on a
capability flag). Reimplementing that from scratch is a much larger, separate project — building
and maintaining a raw PTP/WPD driver rather than using a vendor SDK — and isn't warranted unless
Nikon's own SDK bug goes unresolved.

### Next steps

1. **Report to Nikon SDK support** with this exact repro (camera model, SDK version, event log
   showing `data=1` only). This is their documented capability not behaving as documented.
2. Once the D850's USB mode is fixed (see below), test whether the same repeat-capture pattern
   happens on the legacy MAID3 protocol — if it doesn't, that confirms the bug is confined to
   the v2 wrapper specifically, strengthening the case for the Nikon report and ruling out a
   deeper protocol issue.
3. Check for camera firmware updates — Z6III firmware 2.00 (released around the time of this
   investigation) specifically touches NX Tether interaction and introduces "NX Field," Nikon's
   own corporate remote-shooting system, so this general area is under active firmware
   development.

## Known limitation: D850 not detected via USB

The D850 doesn't appear as a source at all — confirmed not a bug in this codebase, since
Nikon's own official `Type0022Ctrl.exe` sample (built into the SDK download) also reports "There
is no Source object" against the same camera/cable. Windows lists the D850 partly through a USB
Mass Storage interface (`USBSTOR#DISK&VEN_SONY&PROD_QD-G64F`) rather than PTP — check the
camera's **Setup Menu → USB** setting is `MTP/PTP`, not Mass Storage, and re-seat the USB cable
after changing it.
