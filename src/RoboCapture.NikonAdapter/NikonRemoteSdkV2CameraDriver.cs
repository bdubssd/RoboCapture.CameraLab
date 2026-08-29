using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using RoboCapture.Core;
using RoboCapture.NikonAdapter.Native;

namespace RoboCapture.NikonAdapter;

/// <summary>Capture format: JPEG only, RAW only, or both together (RAW+JPEG).</summary>
public enum ImageFormat { Jpeg, Raw, RawAndJpeg }

/// <summary>
/// ICameraDriver implementation over Nikon's "Remote SDK v2" simplified API
/// (ControlServiceLayer.dll, the unified module covering current Z-series bodies).
/// Unlike the legacy per-model MAID3 module, this API exposes a device list and a
/// single synchronous StartShooting call rather than a manual Module/Source/Item/DataObj
/// object graph; captured image bytes are written directly to ImageSavePath.
///
/// Known limitation (see docs/NIKON_SDK_NOTES.md): on the Z6III, StartShooting reliably
/// downloads a file only for the first shot after camera power-on. Every subsequent shot
/// fires the shutter and reports success, but the camera never raises the SDRAM half of
/// kNkMAIDEvent_CaptureComplete, so no bytes are ever offered to this process. This has
/// been isolated to the camera/SDK, not this driver — see that doc for the full diagnosis.
/// </summary>
public sealed class NikonRemoteSdkV2CameraDriver : ICameraDriver, IAsyncDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ShootingTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TransferTimeout = TimeSpan.FromSeconds(30);

    private readonly string _moduleDirectory;
    private readonly string _moduleFileName;
    private readonly BlockingCollection<Action> _work = new();
    private readonly Thread _worker;
    private readonly EventProcDelegate _eventProc;
    private readonly ProgressProcDelegate _progressProc;
    private readonly UiRequestProcDelegate _uiRequestProc;
    private readonly DataProcDelegate _dataProc;
    private readonly LiveViewDataProcDelegate _liveViewDataProc;
    private readonly AllocateMemoryDelegate _allocateMemory;
    private readonly FreeMemoryDelegate _freeMemory;

    private IntPtr _libraryHandle;
    private bool _sdkInitialized;
    private InitializeSdkDelegate? _initializeSdk;
    private FreeSdkDelegate? _freeSdk;
    private ConnectDeviceDelegate? _connectDevice;
    private DisconnectDeviceDelegate? _disconnectDevice;
    private StartShootingDelegate? _startShooting;
    private SetImageVideoSavePathDelegate? _setImageVideoSavePath;
    private SetCapabilityDelegate? _setCapability;
    private GetCapabilityDelegate? _getCapability;
    private StartLiveViewDelegate? _startLiveView;
    private StopLiveViewDelegate? _stopLiveView;
    private GetLiveViewStatusDelegate? _getLiveViewStatus;
    private EnumDevicesDelegate? _enumDevices;
    private uint _connectedDeviceId;

    public string DriverId { get; }
    public CameraConnectionState State { get; private set; } = CameraConnectionState.Disconnected;
    public CameraInfo? Info { get; private set; }
    public event Action<CameraEvent>? Event;

    /// <summary>Raised on each live-view frame (JPEG bytes) while live view is running. Fired
    /// from whatever thread the SDK delivers the frame on — subscribers must marshal to their
    /// own UI thread themselves.</summary>
    public event Action<byte[]>? LiveViewFrame;

    public NikonRemoteSdkV2CameraDriver(string moduleDirectory, string moduleFileName = "ControlServiceLayer.dll")
    {
        _moduleDirectory = moduleDirectory;
        _moduleFileName = moduleFileName;
        DriverId = "nikon.remotesdk.v2";
        _eventProc = HandleEvent;
        _progressProc = (_, _, _, _, _) => { };
        _uiRequestProc = (_, _) => 1; // kNkMAIDUIRequestResult_Ok
        _dataProc = (_, _, _) => 0; // Unused by StartShooting; file bytes arrive via ImageSavePath.
        _liveViewDataProc = HandleLiveViewData;
        _allocateMemory = size => Marshal.AllocHGlobal((int)size);
        _freeMemory = Marshal.FreeHGlobal;
        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "NikonRemoteSdkV2Worker" };
        _worker.Start();
    }

    public Task ConnectAsync(CancellationToken ct = default) => RunOnWorkerAsync(Connect);
    public Task DisconnectAsync(CancellationToken ct = default) => RunOnWorkerAsync(Disconnect);
    public Task<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken ct = default) =>
        RunOnWorkerAsync(() => Capture(request));

    public Task StartLiveViewAsync() => RunOnWorkerAsync(() =>
    {
        if (_startLiveView is null) throw new InvalidOperationException("Camera is not connected.");
        var result = _startLiveView(IntPtr.Zero, IntPtr.Zero);
        if (result < 0) throw new InvalidOperationException($"Failed to start live view (result {result}).");
    });

    public Task StopLiveViewAsync() => RunOnWorkerAsync(() => { _stopLiveView?.Invoke(IntPtr.Zero, IntPtr.Zero); });

    public async ValueTask DisposeAsync()
    {
        if (State != CameraConnectionState.Disconnected)
            await DisconnectAsync();
        await RunOnWorkerAsync(TeardownLibrary);
        _work.CompleteAdding();
        _worker.Join(TimeSpan.FromSeconds(5));
        _work.Dispose();
    }

    private void WorkerLoop()
    {
        foreach (var action in _work.GetConsumingEnumerable())
            action();
    }

    private Task RunOnWorkerAsync(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Add(() =>
        {
            try { action(); completion.SetResult(); }
            catch (Exception exception) { completion.SetException(exception); }
        });
        return completion.Task;
    }

    private Task<T> RunOnWorkerAsync<T>(Func<T> func)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Add(() =>
        {
            try { completion.SetResult(func()); }
            catch (Exception exception) { completion.SetException(exception); }
        });
        return completion.Task;
    }

    private void Connect()
    {
        if (!_sdkInitialized)
        {
            try
            {
                InitializeSdkCore();
            }
            catch
            {
                // A partial failure here (library loaded but InitializeSDK didn't succeed)
                // must not leave _libraryHandle set — otherwise the next Connect() call sees
                // the handle already set, skips re-initializing entirely, and (before this was
                // fixed) fell through to falsely reporting State=Connected with Info still null.
                TeardownLibrary();
                throw;
            }
        }

        // Deliberately NOT reloading the library or re-running InitializeSDK here even on a
        // retry after a failed ConnectDevice: ControlServiceLayer.dll does not tolerate a full
        // unload/reload (FreeSDK+FreeLibrary then LoadLibrary+InitializeSDK again) within one
        // process — confirmed empirically, InitializeSDK reliably returns -117 on the second
        // attempt in the same process after a full teardown, even though a fresh process always
        // succeeds on its first attempt. The library and initialized SDK session are kept alive
        // for this driver's whole lifetime; only DisposeAsync tears them down. See
        // docs/NIKON_SDK_NOTES.md.
        ConnectDeviceCore();

        State = CameraConnectionState.Connected;
        Event?.Invoke(new CameraEvent(DateTimeOffset.UtcNow, DriverId, State, "connect", "success"));
    }

    private void InitializeSdkCore()
    {
        var moduleDirectory = Path.GetFullPath(_moduleDirectory);
        Kernel32.SetDllDirectoryW(moduleDirectory);
        var modulePath = Path.GetFullPath(Path.Combine(moduleDirectory, _moduleFileName));
        _libraryHandle = Kernel32.LoadLibraryW(modulePath);
        if (_libraryHandle == IntPtr.Zero)
            throw new InvalidOperationException(
                $"Failed to load Nikon module '{modulePath}' (Win32 error {Marshal.GetLastWin32Error()}).");

        _initializeSdk = GetDelegate<InitializeSdkDelegate>("InitializeSDK");
        _freeSdk = GetDelegate<FreeSdkDelegate>("FreeSDK");
        _connectDevice = GetDelegate<ConnectDeviceDelegate>("ConnectDevice");
        _disconnectDevice = GetDelegate<DisconnectDeviceDelegate>("DisconnectDevice");
        _startShooting = GetDelegate<StartShootingDelegate>("StartShooting");
        _setImageVideoSavePath = GetDelegate<SetImageVideoSavePathDelegate>("SetImageVideoSavePath");
        _setCapability = GetDelegate<SetCapabilityDelegate>("SetCapability");
        _getCapability = GetDelegate<GetCapabilityDelegate>("GetCapability");
        _startLiveView = GetDelegate<StartLiveViewDelegate>("StartLiveView");
        _stopLiveView = GetDelegate<StopLiveViewDelegate>("StopLiveView");
        _getLiveViewStatus = GetDelegate<GetLiveViewStatusDelegate>("GetLiveViewStatus");
        _enumDevices = GetDelegate<EnumDevicesDelegate>("EnumDevices");

        var callback = new NkMaidCsCallback
        {
            UiReqProc = Marshal.GetFunctionPointerForDelegate(_uiRequestProc),
            EventProc = Marshal.GetFunctionPointerForDelegate(_eventProc),
            ProgressProc = Marshal.GetFunctionPointerForDelegate(_progressProc),
            DataProc = Marshal.GetFunctionPointerForDelegate(_dataProc),
            LiveViewDataProc = Marshal.GetFunctionPointerForDelegate(_liveViewDataProc),
            RefProc = IntPtr.Zero
        };
        var callbackPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NkMaidCsCallback>());
        try
        {
            Marshal.StructureToPtr(callback, callbackPtr, false);
            var allocPtr = Marshal.GetFunctionPointerForDelegate(_allocateMemory);
            var freePtr = Marshal.GetFunctionPointerForDelegate(_freeMemory);
            var initResult = _initializeSdk(allocPtr, freePtr, callbackPtr, out _, IntPtr.Zero);
            if (initResult != 0)
                throw new InvalidOperationException($"Failed to initialize Nikon Remote SDK (result {initResult}).");
            _sdkInitialized = true;
        }
        finally { Marshal.FreeHGlobal(callbackPtr); }
    }

    private static readonly TimeSpan EnumDevicesRetryDelay = TimeSpan.FromSeconds(1.5);
    private const int EnumDevicesAttempts = 3;

    private void ConnectDeviceCore()
    {
        // EnumDevices (not InitializeSDK) is the SDK's documented way to refresh the device
        // list on an already-initialized session — this picks up a camera that was powered on
        // or woken after this driver's SDK session started, without touching the parts of the
        // SDK that don't tolerate being re-entered.
        //
        // A camera that was just woken (half shutter-press after auto power-off, or plugged in
        // moments ago) can take a beat before PTP/USB communication is actually ready even
        // though Windows already shows the device — the first EnumDevices right after that can
        // legitimately come back empty. Retry a few times with a short delay before giving up,
        // rather than making the user click Connect again themselves.
        NkMaidEnumDevices deviceList = default;
        for (var attempt = 1; attempt <= EnumDevicesAttempts; attempt++)
        {
            var enumResult = _enumDevices!(out var deviceListPtr, IntPtr.Zero, IntPtr.Zero);
            if (enumResult != 0)
                throw new InvalidOperationException($"Failed to query Nikon camera list (result {enumResult}).");
            if (deviceListPtr == IntPtr.Zero)
                throw new InvalidOperationException("Nikon Remote SDK returned no device list.");
            deviceList = Marshal.PtrToStructure<NkMaidEnumDevices>(deviceListPtr);
            if (deviceList.Elements > 0) break;
            if (attempt < EnumDevicesAttempts) Thread.Sleep(EnumDevicesRetryDelay);
        }
        if (deviceList.Elements == 0)
            throw new InvalidOperationException(
                "No Nikon camera detected. Check the USB connection, that the camera is powered on, and " +
                "that it hasn't gone to sleep (auto power-off suspends USB communication entirely — wake " +
                "it with a half shutter-press, or disable auto power-off in the camera's menu for tethered sessions).");

        var deviceSize = Marshal.SizeOf<NkMaidDeviceInfo>();
        NkMaidDeviceInfo? selected = null;
        for (var i = 0; i < deviceList.Elements; i++)
        {
            var device = Marshal.PtrToStructure<NkMaidDeviceInfo>(deviceList.DeviceData + i * deviceSize);
            if (device.Availability != 0) { selected = device; break; }
        }
        if (selected is null)
            throw new InvalidOperationException("A Nikon camera is listed but not available for connection.");

        var connectResult = _connectDevice!(selected.Value.Id, IntPtr.Zero);
        if (connectResult != 0)
            throw new InvalidOperationException($"Failed to connect to Nikon camera (result {connectResult}).");

        _connectedDeviceId = selected.Value.Id;
        Info = new CameraInfo("Nikon", selected.Value.Name, selected.Value.Id.ToString(),
            CameraCapabilities.Capture | CameraCapabilities.Download, CameraCompatibilityTier.Full);

        // Cameras default to card-only saving; without SDRAM (host-downloadable) enabled,
        // shooting succeeds but no bytes are ever offered to this process. See the class
        // doc comment: this reliably works for the first shot per power-cycle only.
        SetSaveMedia(0);
        Thread.Sleep(150);
        SetSaveMedia(Maid3V2.SaveMediaCardAndSdram);
    }

    private void TeardownLibrary()
    {
        if (_sdkInitialized && _freeSdk is not null)
        {
            try { _freeSdk(); } catch { /* best effort — we're already tearing down */ }
        }
        _sdkInitialized = false;
        if (_libraryHandle != IntPtr.Zero)
        {
            Kernel32.FreeLibrary(_libraryHandle);
            _libraryHandle = IntPtr.Zero;
        }
        _initializeSdk = null;
        _freeSdk = null;
        _connectDevice = null;
        _disconnectDevice = null;
        _startShooting = null;
        _setImageVideoSavePath = null;
        _setCapability = null;
        _getCapability = null;
        _startLiveView = null;
        _stopLiveView = null;
        _getLiveViewStatus = null;
        _enumDevices = null;
        Info = null;
    }

    private void SetSaveMedia(uint value) => SetUnsignedCapability(Maid3V2.CapSaveMedia, value);

    private void SetUnsignedCapability(uint capabilityId, uint value)
    {
        var valuePtr = Marshal.AllocHGlobal(sizeof(uint));
        try
        {
            Marshal.WriteInt32(valuePtr, (int)value);
            _setCapability!(capabilityId, valuePtr, Maid3.DataTypeUnsignedPtr);
        }
        finally { Marshal.FreeHGlobal(valuePtr); }
    }

    /// <summary>Sets JPEG/RAW/RAW+JPEG capture mode. Safe to call whether or not the camera is
    /// currently connected — if not connected, throws.</summary>
    public Task SetImageFormatAsync(ImageFormat format) => RunOnWorkerAsync(() =>
    {
        if (State != CameraConnectionState.Connected || _setCapability is null)
            throw new InvalidOperationException("Camera is not connected.");
        var value = format switch
        {
            ImageFormat.Raw => Maid3V2.CompressionLevelRaw,
            ImageFormat.RawAndJpeg => Maid3V2.CompressionLevelRawJpegFine,
            _ => Maid3V2.CompressionLevelJpegFine
        };
        SetUnsignedCapability(Maid3V2.CapCompressionLevel, value);
    });

    private void Disconnect()
    {
        if (_stopLiveView is not null && State == CameraConnectionState.Connected)
            _stopLiveView(IntPtr.Zero, IntPtr.Zero);
        if (_disconnectDevice is not null && State == CameraConnectionState.Connected)
            _disconnectDevice();
        // Deliberately NOT calling TeardownLibrary here — see the comment in Connect(). The
        // library stays loaded and the SDK stays initialized so a later reconnect on this same
        // driver instance doesn't hit the unload/reload failure. Only DisposeAsync tears down.
        Info = null;
        State = CameraConnectionState.Disconnected;
        Event?.Invoke(new CameraEvent(DateTimeOffset.UtcNow, DriverId, State, "disconnect", "success"));
    }

    private CaptureResult Capture(CaptureRequest request)
    {
        if (State != CameraConnectionState.Connected || _startShooting is null)
            return Failed("Camera is not connected.");

        Directory.CreateDirectory(request.DestinationFolder);
        var stagingFolder = Path.Combine(request.DestinationFolder, $"nikon-staging-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
        Directory.CreateDirectory(stagingFolder);
        var captureStarted = DateTimeOffset.UtcNow;
        try
        {
            _setImageVideoSavePath?.Invoke(stagingFolder, stagingFolder);
            var shootParam = new MaidShootingStructure
            {
                ShootingType = Maid3V2.ShootingTypeSingle,
                ContinuousIntervalNumShots = 0,
                BulbExposureDuration = 0,
                ShootingStartTimeFromNow = 0,
                IntervalTime = 0,
                AutoFocus = 0,
                ImageSavePath = stagingFolder,
                OutputReference = IntPtr.Zero
            };

            var shootResult = RunWithTimeout(() => _startShooting(ref shootParam, IntPtr.Zero, IntPtr.Zero), ShootingTimeout);
            if (shootResult < 0)
                return Failed($"Shooting command failed (result {shootResult}).", captureStarted);

            var transferStarted = DateTimeOffset.UtcNow;
            var files = Array.Empty<string>();
            var deadline = DateTime.UtcNow + TransferTimeout;
            while (files.Length == 0 && DateTime.UtcNow < deadline)
            {
                files = Directory.GetFiles(stagingFolder);
                if (files.Length == 0) Thread.Sleep(50);
            }

            if (files.Length == 0)
                return new CaptureResult(false, null, null, DateTimeOffset.UtcNow,
                    "No image file appeared after shooting. See docs/NIKON_SDK_NOTES.md — this camera/SDK combination " +
                    "only downloads the first shot per power-cycle; power-cycle the camera and reconnect to retry.",
                    CaptureLifecycleState.ExposureCompleted,
                    DateTimeOffset.UtcNow - captureStarted, DateTimeOffset.UtcNow - transferStarted);

            // RAW+JPEG mode delivers two files per shot. Wait for the file count in the staging
            // folder to stop growing (a short quiet period) before finalizing, so the second file
            // isn't left behind.
            var quietDeadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(800);
            var lastCount = files.Length;
            while (DateTime.UtcNow < quietDeadline)
            {
                Thread.Sleep(150);
                files = Directory.GetFiles(stagingFolder);
                if (files.Length != lastCount)
                {
                    lastCount = files.Length;
                    quietDeadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(800);
                }
            }

            var safeSubject = string.Concat(request.SubjectId.Select(ch =>
                char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_'));
            var movedPaths = new List<string>();
            foreach (var file in files.OrderBy(f => f, StringComparer.Ordinal))
            {
                var cameraFileName = Path.GetFileName(file);
                var localName = $"{safeSubject}_{request.PoseId}_{request.ShotNumber:00}_{cameraFileName}";
                var destPath = Path.Combine(request.DestinationFolder, localName);
                File.Move(file, destPath, overwrite: true);
                movedPaths.Add(destPath);
            }

            // Report the JPEG as the primary path when both were delivered; the RAW file (if
            // any) still lands alongside it in the destination folder.
            var primaryPath = movedPaths.FirstOrDefault(p =>
                p.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                ?? movedPaths[0];

            return new CaptureResult(true, Path.GetFileName(primaryPath), primaryPath, DateTimeOffset.UtcNow, null,
                CaptureLifecycleState.Committed, DateTimeOffset.UtcNow - captureStarted,
                DateTimeOffset.UtcNow - transferStarted);
        }
        finally
        {
            try { if (Directory.Exists(stagingFolder) && Directory.GetFiles(stagingFolder).Length == 0) Directory.Delete(stagingFolder); }
            catch { /* best-effort cleanup */ }
        }
    }

    private static int RunWithTimeout(Func<int> action, TimeSpan timeout)
    {
        var task = Task.Run(action);
        if (!task.Wait(timeout))
            throw new TimeoutException("Nikon Remote SDK shooting call timed out.");
        return task.Result;
    }

    private void HandleLiveViewData(IntPtr refClient, IntPtr liveViewDataPtr)
    {
        try
        {
            if (liveViewDataPtr == IntPtr.Zero) return;
            var imageSize = (uint)Marshal.ReadInt32(liveViewDataPtr, 0);
            if (imageSize == 0 || imageSize > 32 * 1024 * 1024) return; // sanity guard against a bad offset/frame
            var imageDataPtr = Marshal.ReadIntPtr(liveViewDataPtr, Maid3V2.LiveViewImageDataOffset);
            if (imageDataPtr == IntPtr.Zero) return;
            var buffer = new byte[imageSize];
            Marshal.Copy(imageDataPtr, buffer, 0, (int)imageSize);
            LiveViewFrame?.Invoke(buffer);
        }
        catch
        {
            // Never let a malformed frame take down the SDK's callback thread.
        }
    }

    private void HandleEvent(IntPtr refProc, uint eventId, IntPtr data)
    {
        Event?.Invoke(new CameraEvent(DateTimeOffset.UtcNow, DriverId, State, "nativeEvent",
            $"id=0x{eventId:X} data=0x{data.ToInt64():X}"));
    }

    private T GetDelegate<T>(string exportName) where T : Delegate
    {
        var ptr = Kernel32.GetProcAddress(_libraryHandle, exportName);
        if (ptr == IntPtr.Zero)
            throw new InvalidOperationException($"Export '{exportName}' not found in Nikon module.");
        return Marshal.GetDelegateForFunctionPointer<T>(ptr);
    }

    private static CaptureResult Failed(string error, DateTimeOffset? started = null) =>
        new(false, null, null, DateTimeOffset.UtcNow, error, CaptureLifecycleState.Failed,
            started.HasValue ? DateTimeOffset.UtcNow - started.Value : null);
}
