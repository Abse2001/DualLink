using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DualLink.Relay;

internal sealed class LinuxTunDevice : IDisposable
{
    private const int O_RDWR = 2;
    private const int O_CLOEXEC = 0x80000;
    private const uint TUNSETIFF = 0x400454ca;
    private const short IFF_TUN = 0x0001;
    private const short IFF_NO_PI = 0x1000;
    private const int IfReqSize = 40;
    private readonly FileStream _stream;

    public LinuxTunDevice(string name)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("The relay requires Linux.");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 15) throw new ArgumentException("Invalid interface name.", nameof(name));

        var fd = open("/dev/net/tun", O_RDWR | O_CLOEXEC);
        if (fd < 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to open /dev/net/tun");

        var handle = new SafeFileHandle((IntPtr)fd, ownsHandle: true);
        var request = Marshal.AllocHGlobal(IfReqSize);
        try
        {
            Span<byte> bytes = new byte[IfReqSize];
            System.Text.Encoding.ASCII.GetBytes(name, bytes);
            BitConverter.TryWriteBytes(bytes[16..18], (short)(IFF_TUN | IFF_NO_PI));
            Marshal.Copy(bytes.ToArray(), 0, request, IfReqSize);
            if (ioctl(fd, TUNSETIFF, request) < 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to create TUN interface");
        }
        catch
        {
            handle.Dispose();
            throw;
        }
        finally
        {
            Marshal.FreeHGlobal(request);
        }

        // SafeFileHandle instances created from a native Unix fd are synchronous in .NET.
        // FileStream still exposes ReadAsync/WriteAsync using async-over-sync without
        // incorrectly treating this descriptor as Windows-style overlapped I/O.
        _stream = new FileStream(handle, FileAccess.ReadWrite, 64 * 1024, isAsync: false);
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token) => _stream.ReadAsync(buffer, token);
    public ValueTask WriteAsync(ReadOnlyMemory<byte> packet, CancellationToken token) => _stream.WriteAsync(packet, token);
    public void Dispose() => _stream.Dispose();

    [DllImport("libc", SetLastError = true)] private static extern int open(string path, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int ioctl(int fd, uint request, IntPtr data);
}
