using System.Runtime.InteropServices;
using HidSharp;
using Microsoft.Win32.SafeHandles;

namespace RadioBridge.Core.Panel;

/// <summary>
/// Asks the panel for its current input report (HID GET_REPORT). The panel only sends reports when
/// something changes, so without this the selector positions stay unknown, and the displays blank,
/// until the user moves a control. HidSharp has no API for it, hence the platform calls. Best
/// effort: returns null where unsupported (macOS) or when the device refuses.
/// </summary>
internal static partial class CurrentInputReport
{
    /// <summary>The 3 data bytes (without report id), or null.</summary>
    public static byte[]? TryRead(HidDevice device)
    {
        try
        {
            var buffer = new byte[Math.Max(device.GetMaxInputReportLength(), RadioPanelProtocol.InputReportLength + 1)];
            bool ok = OperatingSystem.IsWindows() ? ReadWindows(device.DevicePath, buffer)
                : OperatingSystem.IsLinux() && ReadLinux(device.DevicePath, buffer);
            return ok ? buffer[1..(RadioPanelProtocol.InputReportLength + 1)] : null; // [0] is the report id
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or IOException or UnauthorizedAccessException)
        {
            return null;
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
            // HIDIOCGINPUT(len) = _IOC(_IOC_READ | _IOC_WRITE, 'H', 0x0A, len), Linux 5.11+.
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
