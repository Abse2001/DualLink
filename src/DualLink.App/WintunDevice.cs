using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DualLink.App;

internal sealed class WintunDevice : IDisposable
{
    private const int ErrorNoMoreItems = 259;
    private readonly IntPtr _library;
    private readonly IntPtr _adapter;
    private readonly IntPtr _session;
    private readonly EventWaitHandle _readEvent;
    private readonly WintunCloseAdapterDelegate _closeAdapter;
    private readonly WintunEndSessionDelegate _endSession;
    private readonly WintunReceivePacketDelegate _receivePacket;
    private readonly WintunReleaseReceivePacketDelegate _releaseReceivePacket;
    private readonly WintunAllocateSendPacketDelegate _allocateSendPacket;
    private readonly WintunSendPacketDelegate _sendPacket;

    public WintunDevice(string name = "DualLink Bond")
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Wintun requires Windows.");
        _library = NativeLibrary.Load(System.IO.Path.Combine(AppContext.BaseDirectory, "wintun.dll"));
        var createAdapter = Load<WintunCreateAdapterDelegate>("WintunCreateAdapter");
        _closeAdapter = Load<WintunCloseAdapterDelegate>("WintunCloseAdapter");
        var startSession = Load<WintunStartSessionDelegate>("WintunStartSession");
        _endSession = Load<WintunEndSessionDelegate>("WintunEndSession");
        var getReadWaitEvent = Load<WintunGetReadWaitEventDelegate>("WintunGetReadWaitEvent");
        _receivePacket = Load<WintunReceivePacketDelegate>("WintunReceivePacket");
        _releaseReceivePacket = Load<WintunReleaseReceivePacketDelegate>("WintunReleaseReceivePacket");
        _allocateSendPacket = Load<WintunAllocateSendPacketDelegate>("WintunAllocateSendPacket");
        _sendPacket = Load<WintunSendPacketDelegate>("WintunSendPacket");

        var requestedGuid = new Guid("d4ee322a-80cf-4377-b7a9-fbe079e2ba33");
        var guidPointer = Marshal.AllocHGlobal(Marshal.SizeOf<Guid>());
        try
        {
            Marshal.StructureToPtr(requestedGuid, guidPointer, false);
            _adapter = createAdapter(name, "DualLink", guidPointer);
        }
        finally { Marshal.FreeHGlobal(guidPointer); }
        if (_adapter == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to create the DualLink Wintun adapter");

        _session = startSession(_adapter, 4 * 1024 * 1024);
        if (_session == IntPtr.Zero)
        {
            _closeAdapter(_adapter);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to start the DualLink Wintun session");
        }

        _readEvent = new EventWaitHandle(false, EventResetMode.AutoReset);
        _readEvent.SafeWaitHandle = new SafeWaitHandle(getReadWaitEvent(_session), ownsHandle: false);
    }

    public async ValueTask<byte[]> ReceiveAsync(CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var packet = _receivePacket(_session, out var size);
            if (packet != IntPtr.Zero)
            {
                try
                {
                    var managed = new byte[size];
                    Marshal.Copy(packet, managed, 0, checked((int)size));
                    return managed;
                }
                finally { _releaseReceivePacket(_session, packet); }
            }

            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNoMoreItems) throw new Win32Exception(error, "Unable to read a Wintun packet");
            await Task.Run(() => _readEvent.WaitOne(100), token);
        }
    }

    public void Send(ReadOnlySpan<byte> packet)
    {
        var destination = _allocateSendPacket(_session, checked((uint)packet.Length));
        if (destination == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Wintun send ring is full");
        var managed = packet.ToArray();
        Marshal.Copy(managed, 0, destination, managed.Length);
        _sendPacket(_session, destination);
    }

    public void Dispose()
    {
        _readEvent.Dispose();
        _endSession(_session);
        _closeAdapter(_adapter);
        NativeLibrary.Free(_library);
    }

    private T Load<T>(string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));

    [UnmanagedFunctionPointer(CallingConvention.Winapi, SetLastError = true)]
    private delegate IntPtr WintunCreateAdapterDelegate([MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string tunnelType, IntPtr requestedGuid);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void WintunCloseAdapterDelegate(IntPtr adapter);
    [UnmanagedFunctionPointer(CallingConvention.Winapi, SetLastError = true)] private delegate IntPtr WintunStartSessionDelegate(IntPtr adapter, uint capacity);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void WintunEndSessionDelegate(IntPtr session);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr WintunGetReadWaitEventDelegate(IntPtr session);
    [UnmanagedFunctionPointer(CallingConvention.Winapi, SetLastError = true)] private delegate IntPtr WintunReceivePacketDelegate(IntPtr session, out uint packetSize);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void WintunReleaseReceivePacketDelegate(IntPtr session, IntPtr packet);
    [UnmanagedFunctionPointer(CallingConvention.Winapi, SetLastError = true)] private delegate IntPtr WintunAllocateSendPacketDelegate(IntPtr session, uint packetSize);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void WintunSendPacketDelegate(IntPtr session, IntPtr packet);
}
