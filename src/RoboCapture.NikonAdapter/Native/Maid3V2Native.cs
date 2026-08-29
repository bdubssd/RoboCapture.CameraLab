using System.Runtime.InteropServices;

namespace RoboCapture.NikonAdapter.Native;

// Nikon "Remote SDK v2" simplified API (ControlServiceLayer.dll, Z-series unified module).
// Layered on top of the same MAID3 primitives (DataProc/EventProc/ProgressProc/UIRequestProc)
// but replaces the manual Module/Source/Item/DataObj object graph with a device-list +
// connect/shoot surface.

[StructLayout(LayoutKind.Sequential, Pack = 2, CharSet = CharSet.Ansi)]
internal struct NkMaidDeviceInfo
{
    public uint Id;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Name;
    public byte Availability;
    public uint ConnectedPid;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Version;
}

[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct NkMaidEnumDevices
{
    public uint Elements;
    public uint Value;
    public IntPtr DeviceData;
}

[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct NkMaidCsCallback
{
    public IntPtr UiReqProc;
    public IntPtr EventProc;
    public IntPtr ProgressProc;
    public IntPtr DataProc;
    public IntPtr LiveViewDataProc;
    public IntPtr RefProc;
}

[StructLayout(LayoutKind.Sequential, Pack = 2, CharSet = CharSet.Unicode)]
internal struct MaidShootingStructure
{
    public uint ShootingType;
    public uint ContinuousIntervalNumShots;
    public uint BulbExposureDuration;
    public uint ShootingStartTimeFromNow;
    public uint IntervalTime;
    public byte AutoFocus;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 1024)] public string ImageSavePath;
    public IntPtr OutputReference;
}

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate void LiveViewDataProcDelegate(IntPtr refClient, IntPtr liveViewData);

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate IntPtr AllocateMemoryDelegate(UIntPtr size);

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate void FreeMemoryDelegate(IntPtr memory);

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int InitializeSdkDelegate(IntPtr allocMemory, IntPtr freeMemory, IntPtr callback,
    out IntPtr ppDeviceList, IntPtr ppEnumCapInfo);

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int FreeSdkDelegate();

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int ConnectDeviceDelegate(uint deviceId, IntPtr ppEnumCapInfo);

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int DisconnectDeviceDelegate();

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int StartShootingDelegate(ref MaidShootingStructure shootParam, IntPtr pProc, IntPtr nkRef);

[UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
internal delegate int SetImageVideoSavePathDelegate(string imageSavePath, string videoSavePath);

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int SetCapabilityDelegate(uint capabilityId, IntPtr data, uint dataType);

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int GetCapabilityDelegate(uint capabilityId, uint requestType, out IntPtr dataPtr, out uint dataType);

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int GetShootingStatusDelegate(out uint status);

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int StartLiveViewDelegate(IntPtr pProc, IntPtr nkRef);

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int StopLiveViewDelegate(IntPtr pProc, IntPtr nkRef);

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int GetLiveViewStatusDelegate(out uint status);

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int EnumDevicesDelegate(out IntPtr ppDeviceList, IntPtr pProc, IntPtr nkRef);

internal static class Maid3V2
{
    internal const uint ShootingTypeSingle = 1;

    // kNkMAIDCapability_SaveMedia (0x8305): 0=Card, 1=SDRAM (host-downloadable), 2=Card+SDRAM.
    internal const uint CapSaveMedia = 0x8305;
    internal const uint SaveMediaSdram = 1;
    internal const uint SaveMediaCardAndSdram = 2;

    // kNkMAIDCapability_CompressionLevel (0x8110): the camera's image quality/RAW mode.
    // Full eCompressionLevel enum has Basic/Normal/Fine x HighQuality variants for both JPEG
    // and RAW+JPEG; these three are the ones exposed in the UI (Fine quality in both cases).
    internal const uint CapCompressionLevel = 0x8110;
    internal const uint CompressionLevelJpegFine = 4;
    internal const uint CompressionLevelRaw = 6;
    internal const uint CompressionLevelRawJpegFine = 11;

    // Byte offset of NkMAIDLiveViewData.pImageData, computed by hand from tagLiveViewHeader's
    // field list in Maid3.h (884 bytes under #pragma pack(2)) plus the three fields preceding
    // it (ulLvImageSize:4, wPhysicalBytes:2, wLogicalBits:2). Not represented as a full C#
    // struct — the header carries live-view telemetry (AF points, angles, etc.) this driver
    // doesn't use, and hand-porting ~30 nested fields precisely is riskier than computing the
    // one offset we actually need.
    internal const int LiveViewImageDataOffset = 4 + 2 + 2 + 884;
}
