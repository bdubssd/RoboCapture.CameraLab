using System.Runtime.InteropServices;

namespace RoboCapture.NikonAdapter.Native;

[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct NkMaidObject
{
    public uint Type;
    public uint Id;
    public IntPtr RefClient;
    public IntPtr RefModule;
}

[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct NkMaidCallback
{
    public IntPtr Proc;
    public IntPtr RefProc;
}

[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct NkMaidEnum
{
    public uint ArrayType;
    public uint Elements;
    public uint Value;
    public uint Default;
    public short PhysicalBytes;
    public IntPtr Data;
}

[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct NkMaidFileInfo
{
    public uint DataObjType;
    public uint FileDataType;
    public uint TotalLength;
    public uint Start;
    public uint Length;
    public int DiskFile;
    public int RemoveObject;
}

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int EntryPointDelegate(IntPtr pObject, uint command, uint param, uint dataType, IntPtr data, IntPtr completionProc, IntPtr completionRef);

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate void CompletionProcDelegate(IntPtr pObject, uint command, uint param, uint dataType, IntPtr data, IntPtr refComplete, int result);

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate void EventProcDelegate(IntPtr refProc, uint eventId, IntPtr data);

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int DataProcDelegate(IntPtr refClient, IntPtr dataInfo, IntPtr data);

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate void ProgressProcDelegate(uint command, uint param, IntPtr refProc, uint done, uint total);

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate uint UiRequestProcDelegate(IntPtr refProc, IntPtr uiRequest);

internal static class Kernel32
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr LoadLibraryW(string lpFileName);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool FreeLibrary(IntPtr hModule);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetDllDirectoryW(string? lpPathName);
}

// Numeric values transcribed from Maid3.h / Maid3d1.h (Nikon MAID 3.0 SDK, spec v3.1 r19).
internal static class Maid3
{
    internal const uint CommandAsync = 0;
    internal const uint CommandOpen = 1;
    internal const uint CommandClose = 2;
    internal const uint CommandGetCapCount = 3;
    internal const uint CommandGetCapInfo = 4;
    internal const uint CommandCapStart = 5;
    internal const uint CommandCapSet = 6;
    internal const uint CommandCapGet = 7;
    internal const uint CommandCapGetDefault = 8;
    internal const uint CommandCapGetArray = 9;
    internal const uint CommandEnumChildren = 13;

    internal const uint DataTypeNull = 0;
    internal const uint DataTypeUnsigned = 3;
    internal const uint DataTypeUnsignedPtr = 6;
    internal const uint DataTypeStringPtr = 11;
    internal const uint DataTypeCallbackPtr = 13;
    internal const uint DataTypeEnumPtr = 16;
    internal const uint DataTypeObjectPtr = 17;

    internal const int ResultNoError = 0;

    internal const uint DataObjTypeImage = 0x00000001;
    internal const uint DataObjTypeThumbnail = 0x00000008;
    internal const uint DataObjTypeFile = 0x00000010;

    internal const uint FileDataTypeJpeg = 1;
    internal const uint FileDataTypeTiff = 2;
    internal const uint FileDataTypeNif = 4;

    internal const uint CapProgressProc = 2;
    internal const uint CapEventProc = 3;
    internal const uint CapDataProc = 4;
    internal const uint CapUiRequestProc = 5;
    internal const uint CapChildren = 7;
    internal const uint CapName = 9;
    internal const uint CapCapture = 17;
    internal const uint CapAcquire = 20;
    internal const uint CapModuleType = 54;
    internal const uint CapVersion = 58;

    // eNkMAIDCapabilityD1 (kNkMAIDCapability_VendorBaseDX2 = 0x8100 based)
    internal const uint CapModuleMode = 0x8101;
    internal const uint CapCameraType = 0x81d7;

    internal const uint ModuleModeController = 1;
}
