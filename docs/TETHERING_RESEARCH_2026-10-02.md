# RoboCapture tethering research and implementation recommendation

Research date: 2026-10-02. Scope: Windows-first DSLR and mirrorless still-photo tethering. Findings are based on public manufacturer documentation, integration documentation, compatibility tables, and inspection of the current RoboCapture source. This is a technical recommendation, not a hardware-tested reliability ranking. No installed-base percentage is claimed.

## Recommendation

Build a common camera service with interchangeable backends: official manufacturer SDKs, a documented commercial cross-brand bridge, and a dependable watched-folder import mode. Prioritize Nikon and Canon native integrations; evaluate Smart Shooter for rapid Canon/Nikon/Sony/Fujifilm coverage; add Sony native and legacy support deliberately. Offer other brands according to actual customer bodies and verified capabilities.

Camera tethering does not require AI training per body. It requires supported commands, model discovery, capability negotiation, compatible firmware, and validation. Special lens/focus calibration is a separate concern. A family adapter should discover models and expose only supported operations; narrowly scoped model/firmware exceptions remain necessary.

## Evidence and options

| Route | Strength | Limitation | Recommendation |
|---|---|---|---|
| Official SDKs | Direct control and manufacturer-defined behavior | Multiple integrations, runtime dependencies, firmware differences | Long-term foundation for core brands |
| Smart Shooter API | Documented cross-brand commands and events | Paid dependency; version and API entitlement must be confirmed | Best commercial bridge candidate for a proof of concept |
| digiCamControl | Windows-oriented open-source code and automation | Coverage and features vary by body | Evaluate as a secondary bridge and implementation reference |
| libgphoto2 | Broad open-source camera protocol work | Windows driver deployment can conflict with existing camera access | Prefer optional Linux camera gateway |
| Vendor application plus folder ingestion | Reuses an existing working transfer workflow | Import alone does not supply shutter, settings, or live view control | Broad fallback with an explicit import-only label |
| Generic PTP/MTP | Useful transport and file access | A detected device does not imply remote shooting support | Infrastructure, not a universal tethering solution |

### Canon

Canon provides EDSDK for direct camera control and CCAPI for network control on supported bodies. Use USB EDSDK as the first Canon backend; assess CCAPI separately for specific network deployments. The control compatibility chart must be distinguished from Canon's RAW-processing chart. SDK access and redistribution terms must be checked for the selected package.

Resources: [Canon SDK portal](https://www.usa.canon.com/support/sdk), [EDSDK control compatibility](https://developercommunity.usa.canon.com/resource/1744392420000/CDC_EDSDK_Compat_List), [Canon developer resources](https://asia.canon/en/campaign/developerresources/camera/cap).

### Nikon

Nikon announced unified Remote Module SDK 2.0.0 on March 31, 2026. Its listed bodies include Z9, Z8, Z6III, Z7II, Z6II, Z7, Z6, Z5II, Z5, Zf, Z50II, Z50, Z30, Zfc, and ZR. That release ends Windows 10 support. Legacy DSLR support remains a separate SDK/module concern. Nikon's NEF/NRW image-decoding SDK is not the tethering SDK.

For RoboCapture, retain Z6III support and validate the installed package against the public release. Keep D850/legacy handling distinct where required. Successful initialization alone does not certify repeated capture and transfer.

Resources: [Nikon SDK release history and FAQ](https://sdk.nikonimaging.com/information/en/), [SDK application](https://sdk.nikonimaging.com/apply/), [NX Tether compatibility](https://www.nikonimgsupport.com/eu/BV_article?articleNo=000050786&configured=1&lang=en_GB).

### Sony

Sony Camera Remote SDK is the preferred high-level route for supported newer bodies. Its public page lists version 2.02 and Windows 11 Intel/AMD requirements. Do not assume older a7 III, a7 II, or a6400 bodies are covered by the same SDK.

Sony's separate Camera Remote Command documentation includes those older cameras. Access is restricted to corporate customers, and transport/functions vary by model. Sony warns that Camera Control PTP 2 commands may become unusable on some models from 2027. Follow the current package README before choosing a command dialect. Sony also corrected erroneous per-model command checkmarks in 2025, reinforcing the need for feature-level checks.

Use the modern SDK where supported; evaluate the documented command route or commercial bridge for older bodies. Imaging Edge Remote can save tethered images into a chosen computer folder, providing a practical import fallback.

Resources: [Sony Camera Remote SDK](https://support.d-imaging.sony.co.jp/app/sdk/en/index.html), [Sony Camera Remote Command](https://support.d-imaging.sony.co.jp/app/cameraremotecommand/en/index.html), [Imaging Edge Remote shooting](https://support.d-imaging.sony.co.jp/app/imagingedge/en/instruction/4_5_remote.php).

### Fujifilm

Fujifilm publishes a camera-control SDK for selected X and GFX bodies, including X-T3/4/5, X-H2/H2S, X-S20 and several GFX models. Check firmware and connection requirements against the package. The public SDK page carries a significant condition: individual SDK use to connect/control cameras voids the limited camera warranty; a business application route is provided. Establish the applicable business terms before adoption.

Fujifilm Tether App compatibility does not always mean capture control: entries including X-T50, X-T30 variants, X-E variants, and X100 variants can be backup/restore-only. Never convert that list directly into RoboCapture's tethering support list.

Resources: [Fujifilm SDK and conditions](https://www.fujifilm-x.com/global/camera-control-sdk/), [Tether App feature compatibility](https://www.fujifilm-x.com/en-gb/support/compatibility/software/tether-app/).

### Panasonic, Pentax, OM System, and specialty systems

Panasonic publishes beta Windows SDK packages. The official list includes S1 variants, S5/S5II/S5IIX, S9, GH variants and G9 variants, with a separate package for BGH1/BS1H. The page describes an older development environment and limited support; prove Windows 11 operation and the exact requested body before committing to a native integration. LUMIX Tether support should be checked independently.

Ricoh's official .NET USB SDK appendix documents K-1 II, KP, K-1 and 645Z with a feature matrix. This does not establish support for every newer Pentax. Its known-issues page records a RAW+ save-to-both image-size issue under fast shooting. Current package availability and terms still require verification.

OM Capture provides a vendor tethering workflow. A broadly maintained public interchangeable-body control SDK was not established in this research. Start with verified OM Capture folder output. For Leica, Hasselblad and Phase One, use exact-model vendor application or established tethering application support as the initial route; public evidence gathered here does not establish a universal embeddable SDK for those systems.

Resources: [Panasonic SDK](https://av.jpn.support.panasonic.com/support/software/tool/sdk.html), [Ricoh .NET USB compatibility](https://ricohapi.github.io/docs/camera-usb-sdk-dotnet/appendix/), [Ricoh known issues](https://ricohapi.github.io/docs/camera-usb-sdk-dotnet/known-issues/), [OM Capture](https://download.omsystem.com/pages/oc1download/en/), [OM tethering example](https://learnandsupport.getolympus.com/ca-en/learn-center/photography-tips/technique/tips-on-tethered-shooting), [Capture One model/feature matrix](https://support.captureone.com/hc/en-us/articles/360002718118-Camera-Models-and-RAW-Files-Supported-by-Capture-One).

### Smart Shooter: strongest commercial integration candidate

Smart Shooter documents a JSON/ZeroMQ request/reply channel and event publisher. Its API provides camera discovery, shooting, downloads, property operations and live-view-related facilities. Its version 6 camera list spans Canon, Nikon, Sony and Fujifilm, including Nikon D850 and Z6III. This is a documented integration surface suitable for a C# bridge.

There is a public version-status inconsistency: the download page offers v6.5 dated September 22, 2026 while also saying version 6 remains in testing and purchase is forthcoming. Confirm production availability, exact supported cameras, API entitlement, commercial deployment/redistribution rights, offline activation and support arrangements. Do not assume a version 5 license/API tier conveys version 6 functionality or that a desktop license permits embedding.

A proof of concept should test discovery, trigger, live view, RAW+JPEG receipt, settings, disconnect recovery and event reconciliation. Treat published events as notifications rather than a durable transaction log. Bind a local integration endpoint to loopback unless a secured network deployment is intentionally designed.

Resources: [External API](https://smartshooter.com/v6/external_api.html), [Detailed v6 API schema](https://github.com/kuvacode/smartshooter-api/blob/v6/external_api.rst), [Supported cameras](https://smartshooter.com/v6/supported_cameras.html), [Release notes](https://smartshooter.com/v6/release_notes.html), [Download and purchase-status page](https://kuvacode.com/smartshooter6/).

### Open-source and operating-system routes

digiCamControl is a Windows/C# project with a body-level feature table and local web automation interface. It is worth testing for legacy coverage and studying its device abstraction. Its own MIT license does not grant redistribution rights to every vendor SDK binary it uses. Validate the selected release on actual modern bodies rather than inferring support from the project name.

libgphoto2 offers extensive camera-protocol support under LGPL terms. Consult its remote-control feature table, not only its supported/downloadable-camera list. Windows implementations can require libusb driver changes; a primary-project issue documents interference with Explorer camera access. A Linux mini-PC gateway can isolate that deployment complexity while exposing a controlled network service to RoboCapture. This adds a device and recovery boundary, so it is an optional route rather than the default Windows installation.

Microsoft WPD permits MTP vendor-extension commands through its supported transport. It does not supply the vendor camera-control dialect or make arbitrary cameras remotely shootable. HDMI/UVC preview similarly does not establish original RAW still capture/download support.

Resources: [digiCamControl camera matrix](https://digicamcontrol.com/cameras), [Web automation](https://digicamcontrol.com/doc/userguide/web), [Source and license](https://github.com/dukus/digiCamControl), [libgphoto2 source](https://github.com/gphoto/libgphoto2), [Remote shooting features](https://gphoto.sourceforge.io/doc/remote/), [Generic supported-device list](https://gphoto.sourceforge.io/proj/libgphoto2/support.php), [Windows driver discussion](https://github.com/gphoto/libgphoto2/issues/706), [Microsoft WPD extensions](https://learn.microsoft.com/en-us/windows/win32/wpd_sdk/supporting-mtp-extensions).

## What RoboCapture needs before expanding support

Source review identified these concrete issues in the current checkout:

- `src/RoboCapture.Core/CameraContracts.cs` provides a useful driver abstraction, but discovery, live view, image formats and adjustable settings need shared contracts. A capture result should carry an artifact collection, not only a single local path.
- `src/RoboCapture.CameraLab/MainWindow.cs` contains Nikon-specific driver casts and hard-coded profiles. Move backend selection and feature availability into a camera service.
- `src/RoboCapture.NikonAdapter/NikonRemoteSdkV2CameraDriver.cs:433` starts a native action with Task.Run and times out the wait. This does not cancel the native action. A subsequent reconnect or teardown can overlap an operation still in flight.
- The same driver at line 391 uses 800 ms of unchanged file count for RAW+JPEG completion. This does not prove complete bytes or receipt of every expected artifact.
- At line 412, File.Move uses overwrite:true. Repeated source names must not overwrite a previously committed photograph.
- Existing Nikon notes record historical repeated-transfer problems. They are evidence for further hardware testing, not proof that every current SDK/body combination has that fault.

Recommended service structure:

```text
RoboCapture UI and session/subject workflow
                 |
Camera service: discovery, capability negotiation, exclusive ownership
                 |
Native Nikon | Native Canon | Sony | Smart Shooter | Import-only
                 |
Capture receipts + verified artifact ingestion + recovery ledger
```

Run native backends in supervised helper processes with serial command queues and SDK-required thread affinity. Some SDKs require one process per backend rather than per body; follow each SDK's constraints. A helper process provides a recovery boundary for native hangs/crashes, but termination still leaves physical exposure outcome uncertain.

For every shot, persist camera serial, session, subject, unique request ID and expected outputs before triggering. After a timeout, reconcile camera/files/events before retrying; never blindly repeat a potentially completed exposure. Use temporary destinations, no-overwrite names, transfer-completion signals, size/integrity checks, atomic local commit and a durable receipt. A local hash helps detect later changes and duplicate copies; it does not prove camera-to-PC integrity without a trusted expected digest. Preserve originals until successful commit is established.

Folder import must combine filesystem notifications with periodic scans and a persistent import ledger. FileSystemWatcher can duplicate or lose events. Associate late arrivals with the originating capture/session, not whichever subject is currently selected. If import-only mode cannot establish that association, require explicit grouping or quarantine ambiguous arrivals.

Resource: [Microsoft FileSystemWatcher behavior](https://learn.microsoft.com/en-us/dotnet/api/system.io.filesystemwatcher?view=net-10.0).

## Persistent SDK installation and repeatable releases

Keep versioned, verified runtime packages in an application-managed location independent of source checkout and current working directory. Manifest SDK version, architecture, compatible firmware, required dependencies and file hashes. Install only binaries permitted for redistribution; otherwise locate an approved vendor installation or guide the user through it. Preserve required relative file layouts.

Ship known-good configuration templates where allowed, repair missing/empty required files, and migrate configurations deliberately when versions change. Do not overwrite customized configuration indiscriminately or mix DLLs and profiles from different releases. Keep the previous validated package for rollback. Log selected absolute paths, package version, architecture, camera firmware and initialization result. Never label an unexplained numeric code as a confirmed missing-DLL error.

Before connection, check exclusive camera ownership, correct USB mode, battery/power, required runtime files and process architecture. Prefer a proven direct USB path, secured cable connections and local transfer storage during qualification. Test network transports separately rather than assuming USB results apply.

Resource: [Capture One tethering troubleshooting](https://support.captureone.com/hc/en-us/articles/17686528663709-Tethering-troubleshooting-desktop).

## Qualification and support labels

Maintain a tested matrix keyed by manufacturer, body, firmware, OS, backend/SDK version, connection, file format and relevant storage mode. Record trigger, transfer, live view, settings, autofocus, RAW+JPEG and reconnect separately.

Use explicit labels: manufacturer-listed; detected; capture-tested; production-qualified; import-only. Do not infer full capability from successful connection. A proposed production qualification gate is 1,000 captures per representative configuration, including bursts and long pauses, with zero unexplained missing files, duplicates, overwrites or wrong-subject assignments. This is an engineering acceptance target, not a vendor guarantee or statistical proof of perfection.

Exercise unplug during transfer, application/helper crash, sleep/wake, camera power-cycle, competing application, low battery, full/unwritable destination, slow transfer, filename wraparound, autofocus refusal, card/storage-mode changes and two-camera serial routing. Measure latency distributions and recovery time. Simulator tests cover logic; hardware qualification covers the real integration.

## Delivery order

1. Harden current Nikon capture lifecycle, file receipt and installation diagnostics; qualify Z6III and D850 separately.
2. Generalize camera discovery/capabilities and implement robust import-only mode.
3. Build a Smart Shooter trial integration against a confirmed API-enabled version. In parallel, develop Canon EDSDK as the next native backend.
4. Add Sony SDK support and an explicit route for older Sony bodies. Adopt Fujifilm when business terms and actual target bodies are confirmed.
5. Add Panasonic/Pentax and an optional libgphoto2 gateway when customer demand justifies their validation cost.

This sequence targets widely encountered DSLR/mirrorless families without claiming a numerical market-coverage percentage. No camera vendor guarantees that every function works on every body. The durable investment is the common service, capability matrix and reliable file lifecycle.

## Remaining evidence limits

No new backend was installed or benchmarked during this research. Gated SDK packages, vendor contracts, current API quotations and camera hardware remain necessary to close deployment questions. Capture One and Lightroom are useful independent compatibility references and fallback applications, but a publicly documented embeddable Windows camera-control SDK was not established for either here. RAW decoding support must never be substituted for tether support.

Additional reference: [Adobe's actual tethered-camera list](https://helpx.adobe.com/lightroom-classic/desktop/kb/tethered-camera-support.html).
