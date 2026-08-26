# Testing

Run all tests with `dotnet test RoboCapture.CameraLab.sln` after installing the .NET 8 SDK.

The 1,000-shot simulator acceptance command is:

    dotnet run --project src/RoboCapture.CameraLab -- --stress=1000

Fault paths can be exercised with `--capture-failure=0.1` and `--transfer-failure=0.1`. The simulator uses a seed by default, so failure runs are repeatable.

The automated suite also covers capture and transfer timeouts, cancellation, disconnect/reconnect, pose ordering, SQLite recovery, audit events, and JSON report serialization.

Roster parsing accepts `StudentID`, `FirstName`, `LastName`, `Grade`, `Homeroom`, `Barcode`, and `Team`. Additional CSV columns are retained as custom fields, and quoted comma-containing values are supported.