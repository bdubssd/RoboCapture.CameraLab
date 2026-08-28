using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using RoboCapture.Core;
using RoboCapture.NikonAdapter.Native;

namespace RoboCapture.NikonAdapter;

/// <summary>
/// ICameraDriver implementation over Nikon's MAID 3.0 SDK (Camera Remote SDK).
/// Targets the legacy per-model module (e.g. Type0022.md3 for the D850); the module file loaded
/// determines which physical camera this driver instance talks to.
/// </summary>
public sealed class NikonCameraDriver : ICameraDriver, IAsyncDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan TransferTimeout = TimeSpan.FromSeconds(60);

    private readonly string _moduleDirectory;
    private readonly string _moduleFileName;
    private readonly BlockingCollection<Action> _work = new();
    private readonly Thread _worker;
    private readonly CompletionProcDelegate _completionProc;
    private readonly EventProcDelegate _eventProc;
    private readonly ProgressProcDelegate _progressProc;
    private readonly UiRequestProcDelegate _uiRequestProc;
    private readonly DataProcDelegate _dataProc;

    private IntPtr _libraryHandle;
    private EntryPointDelegate? _entryPoint;
    private IntPtr _moduleObject;
    private IntPtr _sourceObject;
    private DataAccumulator? _activeDownload;

    public string DriverId { get; }
    public CameraConnectionState State { get; private set; } = CameraConnectionState.Disconnected;
    public CameraInfo? Info { get; private set; }
    public event Action<CameraEvent>? Event;

    public NikonCameraDriver(string moduleDirectory, string moduleFileName)
    {
        _moduleDirectory = moduleDirectory;
        _moduleFileName = moduleFileName;
        DriverId = $"nikon.maid3.{Path.GetFileNameWithoutExtension(moduleFileName)}";
        _completionProc = CompletionProc;
        _eventProc = (_, _, _) => { };
        _progressProc = (_, _, _, _, _) => { };
        _uiRequestProc = (_, _) => 1; // kNkMAIDUIRequestResult_Ok
        _dataProc = DataProc;
        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "NikonMaid3Worker" };
        _worker.Start();
    }

    public Task ConnectAsync(CancellationToken ct = default) => RunOnWorkerAsync(Connect);
    public Task DisconnectAsync(CancellationToken ct = default) => RunOnWorkerAsync(Disconnect);
    public Task<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken ct = default) =>
        RunOnWorkerAsync(() => Capture(request));

    public async ValueTask DisposeAsync()
    {
        if (State != CameraConnectionState.Disconnected)
            await DisconnectAsync();
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
        if (_libraryHandle == IntPtr.Zero)
        {
            var moduleDirectory = Path.GetFullPath(_moduleDirectory);
            Kernel32.SetDllDirectoryW(moduleDirectory);
            var modulePath = Path.GetFullPath(Path.Combine(moduleDirectory, _moduleFileName));
            _libraryHandle = Kernel32.LoadLibraryW(modulePath);
            if (_libraryHandle == IntPtr.Zero)
                throw new InvalidOperationException(
                    $"Failed to load Nikon module '{modulePath}' (Win32 error {Marshal.GetLastWin32Error()}).");

            var entryPointPtr = Kernel32.GetProcAddress(_libraryHandle, "MAIDEntryPoint");
            if (entryPointPtr == IntPtr.Zero)
                throw new InvalidOperationException($"MAIDEntryPoint export not found in '{modulePath}'.");
            _entryPoint = Marshal.GetDelegateForFunctionPointer<EntryPointDelegate>(entryPointPtr);
        }

        if (_moduleObject == IntPtr.Zero)
        {
            _moduleObject = AllocObject();
            var openResult = OpenObject(IntPtr.Zero, _moduleObject, 0);
            if (openResult != Maid3.ResultNoError)
                throw new InvalidOperationException($"Failed to open Nikon module object (result {openResult}).");

            var eventProcResult = SetCallback(_moduleObject, Maid3.CapEventProc, _eventProc);
            var progressProcResult = SetCallback(_moduleObject, Maid3.CapProgressProc, _progressProc);
            var uiRequestResult = SetCallback(_moduleObject, Maid3.CapUiRequestProc, _uiRequestProc);
            Event?.Invoke(new CameraEvent(DateTimeOffset.UtcNow, DriverId, State, "setup",
                $"eventProc={eventProcResult} progressProc={progressProcResult} uiRequestProc={uiRequestResult}"));

            // Not every module exposes ModuleMode (Browser/Controller) as a settable capability;
            // legacy single-camera modules can implicitly operate in controller mode already.
            var moduleModeResult = ExecuteAsyncCommand(_moduleObject, Maid3.CommandCapSet, Maid3.CapModuleMode,
                Maid3.DataTypeUnsigned, (IntPtr)Maid3.ModuleModeController, DefaultTimeout);
            Event?.Invoke(new CameraEvent(DateTimeOffset.UtcNow, DriverId, State, "moduleMode", moduleModeResult.ToString()));
        }

        List<uint> sourceIds = [];
        var discoveryDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (sourceIds.Count == 0 && DateTime.UtcNow < discoveryDeadline)
        {
            ExecuteAsyncCommand(_moduleObject, Maid3.CommandEnumChildren, 0, Maid3.DataTypeNull, IntPtr.Zero, DefaultTimeout);
            sourceIds = GetChildIds(_moduleObject);
            if (sourceIds.Count == 0) Thread.Sleep(500);
        }
        if (sourceIds.Count == 0)
            throw new InvalidOperationException(
                "No Nikon camera detected. Check the USB connection, that the camera is powered on, and that no other application (Nikon software, Windows photo import) is holding it.");

        if (_sourceObject == IntPtr.Zero)
        {
            _sourceObject = AllocObject();
            var openResult = OpenObject(_moduleObject, _sourceObject, sourceIds[0]);
            if (openResult != Maid3.ResultNoError)
                throw new InvalidOperationException($"Failed to open Nikon camera source (result {openResult}).");
            SetCallback(_sourceObject, Maid3.CapEventProc, _eventProc);
        }

        var cameraName = GetStringCapability(_sourceObject, Maid3.CapName) ?? "Nikon camera";
        Info = new CameraInfo("Nikon", cameraName, sourceIds[0].ToString(),
            CameraCapabilities.Capture | CameraCapabilities.Download, CameraCompatibilityTier.Full);
        State = CameraConnectionState.Connected;
        Event?.Invoke(new CameraEvent(DateTimeOffset.UtcNow, DriverId, State, "connect", "success"));
    }

    private void Disconnect()
    {
        if (_sourceObject != IntPtr.Zero)
        {
            CloseObject(_sourceObject);
            Marshal.FreeHGlobal(_sourceObject);
            _sourceObject = IntPtr.Zero;
        }
        if (_moduleObject != IntPtr.Zero)
        {
            CloseObject(_moduleObject);
            Marshal.FreeHGlobal(_moduleObject);
            _moduleObject = IntPtr.Zero;
        }
        if (_libraryHandle != IntPtr.Zero)
        {
            Kernel32.FreeLibrary(_libraryHandle);
            _libraryHandle = IntPtr.Zero;
            _entryPoint = null;
        }
        Info = null;
        State = CameraConnectionState.Disconnected;
        Event?.Invoke(new CameraEvent(DateTimeOffset.UtcNow, DriverId, State, "disconnect", "success"));
    }

    private CaptureResult Capture(CaptureRequest request)
    {
        if (State != CameraConnectionState.Connected)
            return Failed("Camera is not connected.");

        var captureStarted = DateTimeOffset.UtcNow;
        var captureResult = ExecuteAsyncCommand(_sourceObject, Maid3.CommandCapStart, Maid3.CapCapture,
            Maid3.DataTypeNull, IntPtr.Zero, CaptureTimeout);
        if (captureResult != Maid3.ResultNoError)
            return Failed($"Capture command failed (result {captureResult}).", captureStarted);

        ExecuteAsyncCommand(_sourceObject, Maid3.CommandEnumChildren, 0, Maid3.DataTypeNull, IntPtr.Zero, DefaultTimeout);
        var itemIds = GetChildIds(_sourceObject);
        if (itemIds.Count == 0)
            return Failed("No image item was produced by the camera.", captureStarted);
        var itemId = itemIds[^1];

        var itemObject = AllocObject();
        try
        {
            var openItemResult = OpenObject(_sourceObject, itemObject, itemId);
            if (openItemResult != Maid3.ResultNoError)
                return Failed($"Failed to open captured item (result {openItemResult}).", captureStarted);

            ExecuteAsyncCommand(itemObject, Maid3.CommandEnumChildren, 0, Maid3.DataTypeNull, IntPtr.Zero, DefaultTimeout);
            var dataIds = GetChildIds(itemObject);
            if (dataIds.Count == 0)
                return Failed("Captured item produced no downloadable data.", captureStarted);

            var dataObject = AllocObject();
            try
            {
                var openDataResult = OpenObject(itemObject, dataObject, dataIds[0]);
                if (openDataResult != Maid3.ResultNoError)
                    return Failed($"Failed to open image data (result {openDataResult}).", captureStarted);

                var transferStarted = DateTimeOffset.UtcNow;
                _activeDownload = new DataAccumulator();
                try
                {
                    SetCallback(dataObject, Maid3.CapDataProc, _dataProc);
                    var acquireResult = ExecuteAsyncCommand(dataObject, Maid3.CommandCapStart, Maid3.CapAcquire,
                        Maid3.DataTypeNull, IntPtr.Zero, TransferTimeout);
                    ExecuteAsyncCommand(dataObject, Maid3.CommandCapSet, Maid3.CapDataProc, Maid3.DataTypeNull,
                        IntPtr.Zero, DefaultTimeout);

                    if (acquireResult != Maid3.ResultNoError || _activeDownload.Buffer is null)
                        return new CaptureResult(false, null, null, DateTimeOffset.UtcNow,
                            $"Image transfer failed (result {acquireResult}).", CaptureLifecycleState.ExposureCompleted,
                            DateTimeOffset.UtcNow - captureStarted, DateTimeOffset.UtcNow - transferStarted);

                    Directory.CreateDirectory(request.DestinationFolder);
                    var extension = FileExtension(_activeDownload.FileDataType);
                    var cameraFileName = $"NIKON_{itemId:000000}{extension}";
                    var safeSubject = string.Concat(request.SubjectId.Select(ch =>
                        char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_'));
                    var localName = $"{safeSubject}_{request.PoseId}_{request.ShotNumber:00}_{cameraFileName}";
                    var path = Path.Combine(request.DestinationFolder, localName);
                    File.WriteAllBytes(path, _activeDownload.Buffer);

                    return new CaptureResult(true, cameraFileName, path, DateTimeOffset.UtcNow, null,
                        CaptureLifecycleState.Committed, DateTimeOffset.UtcNow - captureStarted,
                        DateTimeOffset.UtcNow - transferStarted);
                }
                finally { _activeDownload = null; }
            }
            finally
            {
                CloseObject(dataObject);
                Marshal.FreeHGlobal(dataObject);
            }
        }
        finally
        {
            CloseObject(itemObject);
            Marshal.FreeHGlobal(itemObject);
        }
    }

    private int DataProc(IntPtr refClient, IntPtr dataInfoPtr, IntPtr dataPtr)
    {
        var download = _activeDownload;
        if (download is null) return Maid3.ResultNoError;

        var dataObjType = (uint)Marshal.ReadInt32(dataInfoPtr, 0);
        if ((dataObjType & Maid3.DataObjTypeFile) == 0)
            return Maid3.ResultNoError;

        var fileInfo = Marshal.PtrToStructure<NkMaidFileInfo>(dataInfoPtr);
        download.FileDataType = fileInfo.FileDataType;
        download.Buffer ??= new byte[fileInfo.TotalLength];
        Marshal.Copy(dataPtr, download.Buffer, (int)fileInfo.Start, (int)fileInfo.Length);
        return Maid3.ResultNoError;
    }

    private void CompletionProc(IntPtr pObject, uint command, uint param, uint dataType, IntPtr data, IntPtr refComplete, int result)
    {
        if (refComplete == IntPtr.Zero) return;
        var handle = GCHandle.FromIntPtr(refComplete);
        if (handle.Target is PendingResult pending)
        {
            pending.Result = result;
            pending.Done = true;
        }
    }

    private int ExecuteAsyncCommand(IntPtr pObject, uint command, uint param, uint dataType, IntPtr data, TimeSpan timeout)
    {
        var entryPoint = _entryPoint ?? throw new InvalidOperationException("Nikon module is not loaded.");
        var pending = new PendingResult();
        var handle = GCHandle.Alloc(pending);
        try
        {
            var completionPtr = Marshal.GetFunctionPointerForDelegate(_completionProc);
            entryPoint(pObject, command, param, dataType, data, completionPtr, GCHandle.ToIntPtr(handle));

            var deadline = DateTime.UtcNow + timeout;
            while (!pending.Done)
            {
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException($"Nikon MAID3 command 0x{command:X} (param 0x{param:X}) timed out.");
                entryPoint(pObject, Maid3.CommandAsync, 0, Maid3.DataTypeNull, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                Thread.Sleep(5);
            }
            return pending.Result;
        }
        finally { handle.Free(); }
    }

    private int OpenObject(IntPtr parentObject, IntPtr childObjectPtr, uint childId)
    {
        var entryPoint = _entryPoint ?? throw new InvalidOperationException("Nikon module is not loaded.");
        return entryPoint(parentObject, Maid3.CommandOpen, childId, Maid3.DataTypeObjectPtr, childObjectPtr, IntPtr.Zero, IntPtr.Zero);
    }

    private int CloseObject(IntPtr objectPtr)
    {
        var entryPoint = _entryPoint ?? throw new InvalidOperationException("Nikon module is not loaded.");
        return entryPoint(objectPtr, Maid3.CommandClose, 0, Maid3.DataTypeNull, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
    }

    private int SetCallback(IntPtr objectPtr, uint capability, Delegate callback)
    {
        var callbackStruct = new NkMaidCallback
        {
            Proc = Marshal.GetFunctionPointerForDelegate(callback),
            RefProc = objectPtr
        };
        var callbackPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NkMaidCallback>());
        try
        {
            Marshal.StructureToPtr(callbackStruct, callbackPtr, false);
            return ExecuteAsyncCommand(objectPtr, Maid3.CommandCapSet, capability, Maid3.DataTypeCallbackPtr, callbackPtr, DefaultTimeout);
        }
        finally { Marshal.FreeHGlobal(callbackPtr); }
    }

    private List<uint> GetChildIds(IntPtr objectPtr)
    {
        var enumPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NkMaidEnum>());
        try
        {
            Marshal.StructureToPtr(default(NkMaidEnum), enumPtr, false);
            var getResult = ExecuteAsyncCommand(objectPtr, Maid3.CommandCapGet, Maid3.CapChildren,
                Maid3.DataTypeEnumPtr, enumPtr, DefaultTimeout);
            if (getResult != Maid3.ResultNoError) return [];

            var enumValue = Marshal.PtrToStructure<NkMaidEnum>(enumPtr);
            if (enumValue.Elements == 0 || enumValue.PhysicalBytes != 4) return [];

            var dataPtr = Marshal.AllocHGlobal((int)(enumValue.Elements * enumValue.PhysicalBytes));
            try
            {
                enumValue.Data = dataPtr;
                Marshal.StructureToPtr(enumValue, enumPtr, false);
                var arrayResult = ExecuteAsyncCommand(objectPtr, Maid3.CommandCapGetArray, Maid3.CapChildren,
                    Maid3.DataTypeEnumPtr, enumPtr, DefaultTimeout);
                if (arrayResult != Maid3.ResultNoError) return [];

                var ids = new List<uint>((int)enumValue.Elements);
                for (var i = 0; i < enumValue.Elements; i++)
                    ids.Add((uint)Marshal.ReadInt32(dataPtr, i * 4));
                return ids;
            }
            finally { Marshal.FreeHGlobal(dataPtr); }
        }
        finally { Marshal.FreeHGlobal(enumPtr); }
    }

    private string? GetStringCapability(IntPtr objectPtr, uint capability)
    {
        var bufferPtr = Marshal.AllocHGlobal(256);
        try
        {
            var result = ExecuteAsyncCommand(objectPtr, Maid3.CommandCapGet, capability,
                Maid3.DataTypeStringPtr, bufferPtr, DefaultTimeout);
            return result == Maid3.ResultNoError ? Marshal.PtrToStringAnsi(bufferPtr) : null;
        }
        finally { Marshal.FreeHGlobal(bufferPtr); }
    }

    private static IntPtr AllocObject()
    {
        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<NkMaidObject>());
        Marshal.StructureToPtr(default(NkMaidObject), ptr, false);
        return ptr;
    }

    private static string FileExtension(uint fileDataType) => fileDataType switch
    {
        Maid3.FileDataTypeJpeg => ".jpg",
        Maid3.FileDataTypeTiff => ".tif",
        Maid3.FileDataTypeNif => ".nef",
        _ => ".dat"
    };

    private static CaptureResult Failed(string error, DateTimeOffset? started = null) =>
        new(false, null, null, DateTimeOffset.UtcNow, error, CaptureLifecycleState.Failed,
            started.HasValue ? DateTimeOffset.UtcNow - started.Value : null);

    private sealed class PendingResult
    {
        public volatile bool Done;
        public int Result;
    }

    private sealed class DataAccumulator
    {
        public byte[]? Buffer;
        public uint FileDataType;
    }
}
