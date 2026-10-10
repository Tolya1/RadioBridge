using System.Reflection;
using System.Runtime.InteropServices;

namespace RadioBridge.Core.Sim.Msfs;

/// <summary>
/// The few native SimConnect.dll (x64) functions we need. SimConnect.dll is Microsoft's, so it is not
/// shipped; <see cref="SimConnectLocator"/> finds a copy at run time.
/// </summary>
internal static partial class SimConnectNative
{
    private const string Dll = "SimConnect.dll";

    // SIMCONNECT_RECV_ID
    public const uint RecvException = 1;
    public const uint RecvOpen = 2;
    public const uint RecvQuit = 3;
    public const uint RecvSimObjectData = 8;

    public const uint ObjectIdUser = 0;
    public const uint Unused = 0xFFFFFFFF;
    public const uint DataTypeFloat64 = 4;
    public const uint PeriodSimFrame = 3;
    public const uint DataRequestFlagChanged = 1;
    public const uint GroupPriorityHighest = 1;
    public const uint EventFlagGroupIdIsPriority = 0x10;

    // Byte offsets inside the received structures.
    public const int RecvIdOffset = 8;                // SIMCONNECT_RECV.dwID
    public const int ExceptionCodeOffset = 12;        // SIMCONNECT_RECV_EXCEPTION.dwException
    public const int SimObjectRequestIdOffset = 12;   // SIMCONNECT_RECV_SIMOBJECT_DATA.dwRequestID
    public const int SimObjectDataOffset = 40;        // SIMCONNECT_RECV_SIMOBJECT_DATA.dwData

    private static string? _resolvedPath;

    static SimConnectNative() => NativeLibrary.SetDllImportResolver(typeof(SimConnectNative).Assembly, Resolve);

    /// <summary>Path of the SimConnect.dll that was (or would be) loaded, for diagnostics.</summary>
    public static string? ResolvedPath => _resolvedPath ??= SimConnectLocator.Find();

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!name.Equals(Dll, StringComparison.OrdinalIgnoreCase))
            return IntPtr.Zero;
        return ResolvedPath is { } path && NativeLibrary.TryLoad(path, out var handle) ? handle : IntPtr.Zero;
    }

    [LibraryImport(Dll, EntryPoint = "SimConnect_Open", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int Open(out IntPtr handle, string name, IntPtr hwnd, uint userEventWin32, IntPtr eventHandle, uint configIndex);

    [LibraryImport(Dll, EntryPoint = "SimConnect_Close")]
    public static partial int Close(IntPtr handle);

    [LibraryImport(Dll, EntryPoint = "SimConnect_GetNextDispatch")]
    public static partial int GetNextDispatch(IntPtr handle, out IntPtr data, out uint size);

    [LibraryImport(Dll, EntryPoint = "SimConnect_AddToDataDefinition", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int AddToDataDefinition(IntPtr handle, uint defineId, string datumName, string unitsName, uint datumType, float epsilon, uint datumId);

    [LibraryImport(Dll, EntryPoint = "SimConnect_RequestDataOnSimObject")]
    public static partial int RequestDataOnSimObject(IntPtr handle, uint requestId, uint defineId, uint objectId, uint period, uint flags, uint origin, uint interval, uint limit);

    [LibraryImport(Dll, EntryPoint = "SimConnect_MapClientEventToSimEvent", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int MapClientEventToSimEvent(IntPtr handle, uint eventId, string eventName);

    [LibraryImport(Dll, EntryPoint = "SimConnect_TransmitClientEvent")]
    public static partial int TransmitClientEvent(IntPtr handle, uint objectId, uint eventId, uint data, uint groupId, uint flags);
}
