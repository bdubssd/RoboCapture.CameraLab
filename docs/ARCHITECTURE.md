# RoboCapture Camera Lab 0.2 Architecture

The application is offline-first. `RoboCapture.Core` owns manufacturer-independent camera contracts, pose sequencing, simulator behavior, and stress accounting. Camera implementations are selected through `ICameraDriver`; core code does not branch on manufacturer.

`RoboCapture.Persistence` owns the local SQLite database. Capture records are inserted with an explicit lifecycle state and sessions remain incomplete until explicitly completed. WAL mode supports recovery after process interruption.

The Camera Lab executable is a thin operational shell around the simulator and stress engine. Real vendor SDK adapters are intentionally absent from 0.2.

`CsvRosterImporter` keeps subject identification independent from camera hardware. Standard roster columns map to `SubjectRecord`; unknown columns remain available as custom fields for later station workflows.