using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Net.NetworkInformation;

namespace DualLink.App;

// IPv4 IP Helper API. Route mutations are synchronous kernel calls, not shell
// processes. Updating an existing row preserves the route while UDP sockets roam.
internal static class WindowsRouteTable
{
    [StructLayout(LayoutKind.Explicit, Size = 28)]
    internal struct Address
    {
        [FieldOffset(0)] public ushort Family;
        [FieldOffset(4)] public uint IPv4;
        public static Address From(IPAddress ip) => new() { Family = 2, IPv4 = BitConverter.ToUInt32(ip.GetAddressBytes()) };
    }

    [StructLayout(LayoutKind.Explicit, Size = 104)]
    internal struct Route
    {
        [FieldOffset(0)] public ulong Luid;
        [FieldOffset(8)] public uint InterfaceIndex;
        [FieldOffset(12)] public Address Destination;
        [FieldOffset(40)] public byte PrefixLength;
        [FieldOffset(44)] public Address NextHop;
        [FieldOffset(72)] public byte SitePrefixLength;
        [FieldOffset(76)] public uint ValidLifetime;
        [FieldOffset(80)] public uint PreferredLifetime;
        [FieldOffset(84)] public uint Metric;
        [FieldOffset(88)] public uint Protocol;
    }

    [StructLayout(LayoutKind.Explicit, Size = 168)]
    internal struct Interface
    {
        [FieldOffset(0)] public ushort Family;
        [FieldOffset(8)] public ulong Luid;
        [FieldOffset(16)] public uint Index;
        [FieldOffset(44)] public byte AutomaticMetric;
        [FieldOffset(144)] public uint SitePrefixLength;
        [FieldOffset(148)] public uint Metric;
    }

    [DllImport("iphlpapi.dll")] private static extern uint GetIpForwardTable2(ushort family, out IntPtr table);
    [DllImport("iphlpapi.dll")] private static extern void FreeMibTable(IntPtr table);
    [DllImport("iphlpapi.dll")] private static extern void InitializeIpForwardEntry(out Route row);
    [DllImport("iphlpapi.dll")] private static extern uint CreateIpForwardEntry2(ref Route row);
    [DllImport("iphlpapi.dll")] private static extern uint SetIpForwardEntry2(ref Route row);
    [DllImport("iphlpapi.dll")] private static extern uint DeleteIpForwardEntry2(ref Route row);
    [DllImport("iphlpapi.dll")] private static extern uint GetBestRoute2(IntPtr luid, uint index, IntPtr source,
        ref Address destination, uint options, out Route route, out Address bestSource);
    [DllImport("iphlpapi.dll")] private static extern uint GetIpInterfaceEntry(ref Interface row);
    [DllImport("iphlpapi.dll")] private static extern uint SetIpInterfaceEntry(ref Interface row);

    public static IReadOnlyList<Route> Read()
    {
        Check(GetIpForwardTable2(2, out var table));
        try
        {
            var count = Marshal.ReadInt32(table);
            var rows = new Route[count];
            for (var i = 0; i < count; i++)
                rows[i] = Marshal.PtrToStructure<Route>(IntPtr.Add(table, 8 + i * 104));
            return rows;
        }
        finally { FreeMibTable(table); }
    }

    public static int? BestInterface(IPAddress destination)
    {
        var address = Address.From(destination);
        return GetBestRoute2(IntPtr.Zero, 0, IntPtr.Zero, ref address, 0, out var row, out _) == 0
            ? (int)row.InterfaceIndex : null;
    }

    public static Interface ReadInterface(int index)
    {
        var row = new Interface { Family = 2, Index = (uint)index };
        Check(GetIpInterfaceEntry(ref row));
        return row;
    }

    public static void SetMetric(int index, int metric, bool automatic = false)
    {
        var row = ReadInterface(index);
        if (row.Metric == metric && row.AutomaticMetric == (automatic ? 1 : 0)) return;
        row.Metric = (uint)metric;
        row.AutomaticMetric = automatic ? (byte)1 : (byte)0;
        // Required by SetIpInterfaceEntry for IPv4 even on systems returning a
        // nonzero SitePrefixLength in GetIpInterfaceEntry.
        row.SitePrefixLength = 0;
        Check(SetIpInterfaceEntry(ref row));
    }

    public static void EnsureHost(IPAddress destination, int index, IPAddress gateway, int metric)
    {
        var address = Address.From(destination);
        var nextHop = Address.From(gateway);
        var existing = Read().Where(x => x.InterfaceIndex == index && x.PrefixLength == 32 &&
            x.Destination.IPv4 == address.IPv4).ToArray();
        var matching = existing.FirstOrDefault(x => x.NextHop.IPv4 == nextHop.IPv4);
        if (matching.InterfaceIndex != 0)
        {
            if (matching.Metric != metric)
            {
                matching.Metric = (uint)metric;
                Check(SetIpForwardEntry2(ref matching));
            }
        }
        else
        {
            InitializeIpForwardEntry(out var row);
            row.InterfaceIndex = (uint)index;
            row.Destination = address;
            row.PrefixLength = 32;
            row.NextHop = nextHop;
            row.Metric = (uint)metric;
            row.Protocol = 3;
            var error = CreateIpForwardEntry2(ref row);
            if (error != 5010) Check(error); // An identical row may appear during a DHCP notification.
        }
        // Install the new gateway first, then remove obsolete DHCP gateways.
        foreach (var stale in existing.Where(x => x.NextHop.IPv4 != nextHop.IPv4)) Delete(stale);
    }

    public static void RemoveHosts(IEnumerable<IPAddress> destinations, IEnumerable<int> interfaces)
    {
        var addresses = destinations.Select(x => Address.From(x).IPv4).ToHashSet();
        var indices = interfaces.Select(x => (uint)x).ToHashSet();
        foreach (var row in Read().Where(x => x.PrefixLength == 32 && indices.Contains(x.InterfaceIndex) &&
                     addresses.Contains(x.Destination.IPv4))) Delete(row);
    }

    private static void Delete(Route row)
    {
        var error = DeleteIpForwardEntry2(ref row);
        if (error != 1168 && error != 2) Check(error);
    }

    private static void Check(uint error)
    {
        if (error != 0) throw new Win32Exception((int)error);
    }

    public static void SmokeTest()
    {
        var target = IPAddress.Parse("198.18.0.254"); // Benchmark-only address; no traffic is sent.
        var candidate = NetworkInterface.GetAllNetworkInterfaces().Where(x => x.OperationalStatus == OperationalStatus.Up)
            .Select(x => x.GetIPProperties()).First(x => x.GatewayAddresses.Any(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork));
        var index = candidate.GetIPv4Properties()!.Index;
        var gateway = candidate.GatewayAddresses.First(x => x.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).Address;
        var original = ReadInterface(index);
        if (Read().Any(x => x.PrefixLength == 32 && x.Destination.IPv4 == Address.From(target).IPv4))
            throw new InvalidOperationException("Smoke-test destination already has a host route.");
        try
        {
            SetMetric(index, (int)original.Metric, original.AutomaticMetric == 0);
            if (ReadInterface(index).AutomaticMetric == original.AutomaticMetric)
                throw new InvalidOperationException("Native interface metric update did not round-trip.");
            EnsureHost(target, index, gateway, 17);
            EnsureHost(target, index, gateway, 18);
            var routes = Read().Where(x => x.InterfaceIndex == index && x.PrefixLength == 32 && x.Destination.IPv4 == Address.From(target).IPv4).ToArray();
            if (routes.Length != 1 || routes[0].Metric != 18 || BestInterface(target) != index)
                throw new InvalidOperationException("Native route create/update/lookup did not round-trip.");
        }
        finally
        {
            RemoveHosts([target], [index]);
            SetMetric(index, (int)original.Metric, original.AutomaticMetric != 0);
        }
    }
}
