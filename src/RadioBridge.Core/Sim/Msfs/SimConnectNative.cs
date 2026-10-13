using System.Reflection;
using System.Runtime.InteropServices;

namespace RadioBridge.Core.Sim.Msfs;

/// <summary>
/// The few native SimConnect (x64) functions we need. The library is Microsoft's and is never shipped;
/// <see cref="SimConnectLocator"/> finds a copy on the machine and <see cref="TryLoad"/> loads it.
/// </summary>
internal static partial class SimConnectNative
{
    private const string Dll = "SimConnect.dll";

    // SIMCONNECT_RECV_ID
    public const uint RecvException = 1;
    public const uint RecvOpen = 2;
    public const uint RecvQuit = 3;
    public const uint RecvSimObjectData = 8;
    public const uint RecvSystemState = 15;
    public const int SystemStateRequestIdOffset = 12; // SIMCONNECT_RECV_SYSTEM_STATE.dwRequestID
    public const int SystemStateStringOffset = 24;    // SIMCONNECT_RECV_SYSTEM_STATE.szString[260]
    public const uint RecvEnumerateInputEvents = 34;

    public const uint ObjectIdUser = 0;
    public const uint Unused = 0xFFFFFFFF;
    public const uint DataTypeFloat64 = 4;
    public const uint DataTypeString256 = 9;
    public const uint PeriodSimFrame = 3;
    public const uint PeriodSecond = 4;
    public const uint DataRequestFlagChanged = 1;
    public const uint GroupPriorityHighest = 1;
    public const uint EventFlagGroupIdIsPriority = 0x10;

    // Byte offsets inside the received structures.
    public const int RecvIdOffset = 8;                // SIMCONNECT_RECV.dwID
    public const int ExceptionCodeOffset = 12;        // SIMCONNECT_RECV_EXCEPTION.dwException
    public const int SimObjectRequestIdOffset = 12;   // SIMCONNECT_RECV_SIMOBJECT_DATA.dwRequestID
    public const int SimObjectDataOffset = 40;        // SIMCONNECT_RECV_SIMOBJECT_DATA.dwData

    private static readonly object Gate = new();
    private static IntPtr _library;
    private static string? _loadedPath;

    static SimConnectNative() => NativeLibrary.SetDllImportResolver(typeof(SimConnectNative).Assembly, Resolve);

    /// <summary>The library that is loaded into this process, or null.</summary>
    public static string? LoadedPath
    {
        get { lock (Gate) return _loadedPath; }
    }

    /// <summary>
    /// Loads the library at <paramref name="path"/>. A native library can't be swapped once loaded, so
    /// when one is already loaded this succeeds without changing anything.
    /// </summary>
    public static bool TryLoad(string path, out string? error)
    {
        lock (Gate)
        {
            error = null;
            if (_library != IntPtr.Zero)
                return true;
            try
            {
                _library = NativeLibrary.Load(path);
                _loadedPath = path;
                return true;
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
            {
                error = ex.Message;
                return false;
            }
        }
    }

    /// <summary>
    /// Whether the loaded library has the input events API (MSFS 2020 SU13 and MSFS 2024). Older libraries lack
    /// it, and calling a missing function would end MSFS support, so check first.
    /// </summary>
    public static bool HasInputEvents
    {
        get
        {
            lock (Gate)
            {
                return _library != IntPtr.Zero
                       && NativeLibrary.TryGetExport(_library, "SimConnect_EnumerateInputEvents", out _)
                       && NativeLibrary.TryGetExport(_library, "SimConnect_SetInputEvent", out _);
            }
        }
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!name.Equals(Dll, StringComparison.OrdinalIgnoreCase))
            return IntPtr.Zero;
        lock (Gate)
            return _library;
    }

    [LibraryImport(Dll, EntryPoint = "SimConnect_Open", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int Open(out IntPtr handle, string name, IntPtr hwnd, uint userEventWin32, IntPtr eventHandle, uint configIndex);

    [LibraryImport(Dll, EntryPoint = "SimConnect_Close")]
    public static partial int Close(IntPtr handle);

    [LibraryImport(Dll, EntryPoint = "SimConnect_GetNextDispatch")]
    public static partial int GetNextDispatch(IntPtr handle, out IntPtr data, out uint size);

    [LibraryImport(Dll, EntryPoint = "SimConnect_AddToDataDefinition", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int AddToDataDefinition(IntPtr handle, uint defineId, string datumName, string? unitsName, uint datumType, float epsilon, uint datumId);

    [LibraryImport(Dll, EntryPoint = "SimConnect_RequestDataOnSimObject")]
    public static partial int RequestDataOnSimObject(IntPtr handle, uint requestId, uint defineId, uint objectId, uint period, uint flags, uint origin, uint interval, uint limit);

    [LibraryImport(Dll, EntryPoint = "SimConnect_MapClientEventToSimEvent", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int MapClientEventToSimEvent(IntPtr handle, uint eventId, string eventName);

    [LibraryImport(Dll, EntryPoint = "SimConnect_TransmitClientEvent")]
    public static partial int TransmitClientEvent(IntPtr handle, uint objectId, uint eventId, uint data, uint groupId, uint flags);

    /// <summary>Answered with RecvSystemState; "AircraftLoaded" gives the loaded aircraft's .cfg path.</summary>
    [LibraryImport(Dll, EntryPoint = "SimConnect_RequestSystemState", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int RequestSystemState(IntPtr handle, uint requestId, string state);

    /// <summary>Answered with RecvEnumerateInputEvents pages (see <see cref="GnsInputEvents.ReadPage"/>).</summary>
    [LibraryImport(Dll, EntryPoint = "SimConnect_EnumerateInputEvents")]
    public static partial int EnumerateInputEvents(IntPtr handle, uint requestId);

    /// <param name="hash">The input event's hash: 64 bits, whatever the documentation says.</param>
    /// <param name="size">Bytes of <paramref name="values"/>: 8 per FLOAT64 parameter.</param>
    [LibraryImport(Dll, EntryPoint = "SimConnect_SetInputEvent")]
    public static partial int SetInputEvent(IntPtr handle, ulong hash, uint size, double[] values);
}
