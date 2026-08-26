# Decisions

## 0.2: simulator before vendor SDKs

The simulator is the first test tool because it gives deterministic connection, exposure, transfer, disconnect, and file-accounting behavior without licensing or hardware. Canon, Nikon, Sony, and Panasonic drivers remain future adapters.

## 0.2: local SQLite with WAL

Capture accountability belongs at the station. SQLite is local, works offline, and WAL improves restart behavior without introducing a service dependency.