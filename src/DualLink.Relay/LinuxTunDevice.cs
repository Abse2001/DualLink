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
    private readonly FileStream _readStream;
    private readonly FileStream _writeStream;

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

        var writeFd = dup(fd);
        if (writeFd < 0)
        {
            handle.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to duplicate TUN descriptor");
        }

        // Separate FileStream instances are required for full duplex. A synchronous
        // blocking read and a write on the same FileStream are serialized internally.
        _readStream = new FileStream(handle, FileAccess.Read, 64 * 1024, isAsync: false);
        _writeStream = new FileStream(new SafeFileHandle((IntPtr)writeFd, ownsHandle: true), FileAccess.Write, 64 * 1024, isAsync: false);
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token) => _readStream.ReadAsync(buffer, token);
    public ValueTask WriteAsync(ReadOnlyMemory<byte> packet, CancellationToken token) => _writeStream.WriteAsync(packet, token);
    public void Dispose()
    {
        _readStream.Dispose();
        _writeStream.Dispose();
    }

    [DllImport("libc", SetLastError = true)] private static extern int open(string path, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int dup(int oldfd);
    [DllImport("libc", SetLastError = true)] private static extern int ioctl(int fd, uint request, IntPtr data);
}
