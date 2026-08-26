# Camera API

`ICameraDriver` is asynchronous and cancellation-aware. It exposes connection state, camera identity, capability flags, compatibility tier, capture, and structured events.

Capture results distinguish exposure completion from transfer and local-file commit. Unsupported features are represented by absent capability flags; callers must degrade gracefully rather than identify a manufacturer.

The simulator can impose independent capture and transfer timeouts. A capture timeout has no camera file or local path; a transfer timeout retains `ExposureCompleted` but does not claim a received local file.

QR and barcode input belongs to subject identification, not the camera driver. The WPF lab accepts scanner keyboard input terminated by Enter and uses the scanned value as the subject key. Offline roster resolution is provided by `SubjectIdentifier`.