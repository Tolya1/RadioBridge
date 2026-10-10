using System.Numerics;
using System.Runtime.InteropServices;
using HidSharp;
using Microsoft.Win32.SafeHandles;

namespace RadioBridge.Core.Panel;

/// <summary>
/// Asks the panel for its current input state. The panel only sends reports when something
/// changes, so without this the selector positions stay unknown, and the displays blank, until the
/// user moves a control.
/// <para>
/// The proper call is HID GET_REPORT(Input), which HidSharp doesn't offer: hence HidD_GetInputReport
/// on Windows and HIDIOCGINPUT on Linux 5.11+. Elsewhere (macOS, older kernels) we fall back to
/// GET_REPORT(Feature) through HidSharp: the panel's firmware answers every report request with its
/// input state, which was verified on real hardware.
/// </para>
/// </summary>
internal static partial class CurrentInputReport
{
    /// <summary>The 3 data bytes (without report id), or null when the state could not be read.</summary>
    public static byte[]? TryRead(HidDevice device, HidStream stream)
    {
        int length = RadioPanelProtocol.InputReportLength + 1; // + report id
        var input = new byte[Math.Max(device.GetMaxInputReportLength(), length)];
        if (TryPlatform(device.DevicePath, input) && Plausible(input))
            return input[1..length];

        var feature = new byte[Math.Max(device.GetMaxFeatureReportLength(), length)];
        try
        {
            stream.GetFeature(feature);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or NotSupportedException)
        {
            return null;
        }
        return Plausible(feature) ? feature[1..length] : null;
    }

    /// <summary>A real panel state has at most one selector bit per row, and at least one row set.</summary>
    internal static bool Plausible(byte[] withReportId)
    {
        int state = RadioPanelProtocol.ReadState(withReportId.AsSpan(1));
        int upper = BitOperations.PopCount((uint)(state & 0x7F));
        int lower = BitOperations.PopCount((uint)((state >> 7) & 0x7F));
        return upper <= 1 && lower <= 1 && upper + lower > 0;
    }

    private static bool TryPlatform(string path, byte[] buffer)
    {
        try
        {
            return OperatingSystem.IsWindows() ? ReadWindows(path, buffer)
                : OperatingSystem.IsLinux() && ReadLinux(path, buffer);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static bool ReadWindows(string path, byte[] buffer)
    {
        const uint GenericReadWrite = 0xC0000000;
        const uint ShareReadWrite = 3;
        const uint OpenExisting = 3;
        using var handle = CreateFile(path, GenericReadWrite, ShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        return !handle.IsInvalid && HidD_GetInputReport(handle, buffer, buffer.Length);
    }

    private static bool ReadLinux(string path, byte[] buffer)
    {
        if (!path.StartsWith("/dev/hidraw", StringComparison.Ordinal))
            return false;
        const int ReadWrite = 2;
        int fd = Open(path, ReadWrite);
        if (fd < 0)
            return false;
        try
        {
            // HIDIOCGINPUT(len) = _IOC(_IOC_READ | _IOC_WRITE, 'H', 0x0A, len)
            nuint request = (3u << 30) | ((uint)buffer.Length << 16) | ('H' << 8) | 0x0A;
            return Ioctl(fd, request, buffer) >= 0;
        }
        finally
        {
            Close(fd);
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [LibraryImport("hid.dll", EntryPoint = "HidD_GetInputReport")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool HidD_GetInputReport(SafeFileHandle device, [Out] byte[] buffer, int length);

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "ioctl")]
    private static partial int Ioctl(int fd, nuint request, [In, Out] byte[] buffer);

    [LibraryImport("libc", EntryPoint = "close")]
    private static partial int Close(int fd);
}
