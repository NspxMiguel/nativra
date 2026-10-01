using System;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Nativra.X86.Loader
{
    // iphlpapi's adapter list: one Ethernet adapter carrying the host's own
    // IPv4 address — the one the guest's sockets go out on — so a server or a
    // LAN lobby can say where it listens.
    public sealed partial class GuestKernel
    {
        private const uint AdapterSize = 376, UnicastSize = 48, SockaddrInSize = 16;

        private void InstallAdapters(GuestImports i)
        {
            i.Register("iphlpapi.dll", "GetAdaptersAddresses", CallConv.Stdcall, 5, c =>
                AdaptersAddresses(c.Arg(0), c.Arg(3), c.Arg(4)));
        }

        /// <summary>The host's outgoing IPv4 address (no packet is sent to learn it), or loopback.</summary>
        private static IPAddress HostAddress()
        {
            try
            {
                using (var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    probe.Connect(new IPEndPoint(IPAddress.Parse("192.0.2.1"), 9));   // TEST-NET-1: never routed anywhere
                    return ((IPEndPoint)probe.LocalEndPoint).Address;
                }
            }
            catch (SocketException)
            {
                return IPAddress.Loopback;
            }
        }

        // IP_ADAPTER_ADDRESSES_LH (x86, 376 bytes): Length +0, IfIndex +4, Next +8,
        // AdapterName +12, FirstUnicastAddress +16, DnsSuffix +32, Description +36,
        // FriendlyName +40, PhysicalAddress +44, PhysicalAddressLength +52, Flags +56,
        // Mtu +60, IfType +64, OperStatus +68, TransmitLinkSpeed +144,
        // ReceiveLinkSpeed +152, Ipv4Metric +168. IP_ADAPTER_UNICAST_ADDRESS_LH
        // (48): Length +0, Flags +4, Next +8, Address {lpSockaddr, length} +12,
        // origins and DAD state +20..+28, lifetimes +32..+40, prefix length +44.
        private uint AdaptersAddresses(uint family, uint buffer, uint sizePointer)
        {
            const uint AfUnspec = 0, AfInet = 2, ErrorBufferOverflow = 111, ErrorNoData = 232;
            if (sizePointer == 0) return ErrorInvalidParameter;
            if (family != AfUnspec && family != AfInet) return ErrorNoData;   // no IPv6 address is offered

            var name = Encoding.ASCII.GetBytes("{6E61742D-7261-4E61-7469-7672614E4554}\0");
            var description = Encoding.Unicode.GetBytes("Nativra network\0");
            var friendly = Encoding.Unicode.GetBytes("Ethernet\0");
            var empty = new byte[2];
            uint needed = AdapterSize + UnicastSize + SockaddrInSize + (uint)(name.Length + description.Length + friendly.Length + empty.Length);
            needed = (needed + 7) & ~7u;
            var given = memory.Read32(sizePointer);
            memory.Write32(sizePointer, needed);
            if (buffer == 0 || given < needed) return ErrorBufferOverflow;

            memory.WriteBytes(buffer, new byte[needed]);
            uint adapter = buffer, unicast = buffer + AdapterSize, sockaddr = unicast + UnicastSize, text = sockaddr + SockaddrInSize;
            uint Put(byte[] bytes) { var at = text; memory.WriteBytes(at, bytes); text += (uint)bytes.Length; return at; }

            var ip = HostAddress().GetAddressBytes();
            memory.Write16(sockaddr, 2);                       // AF_INET, port 0
            memory.WriteBytes(sockaddr + 4, ip);

            memory.Write32(unicast + 0, UnicastSize);
            memory.Write32(unicast + 12, sockaddr);
            memory.Write32(unicast + 16, SockaddrInSize);
            memory.Write32(unicast + 20, 3);                   // PrefixOrigin: DHCP
            memory.Write32(unicast + 24, 3);                   // SuffixOrigin: DHCP
            memory.Write32(unicast + 28, 4);                   // DadState: preferred
            memory.Write32(unicast + 32, 0xFFFFFFFF);
            memory.Write32(unicast + 36, 0xFFFFFFFF);
            memory.Write32(unicast + 40, 0xFFFFFFFF);
            memory.WriteBytes(unicast + 44, new byte[] { 24 });

            memory.Write32(adapter + 0, AdapterSize);
            memory.Write32(adapter + 4, 2);                    // IfIndex
            memory.Write32(adapter + 12, Put(name));
            memory.Write32(adapter + 16, unicast);
            var none = Put(empty);
            memory.Write32(adapter + 32, none);                // DnsSuffix: ""
            memory.Write32(adapter + 36, Put(description));
            memory.Write32(adapter + 40, Put(friendly));
            memory.WriteBytes(adapter + 44, new byte[] { 0x02, 0x4E, 0x41, 0x54, 0x56, 0x52 });   // locally administered MAC
            memory.Write32(adapter + 52, 6);
            memory.Write32(adapter + 56, 0x1 | 0x100);         // DDNS enabled, IPv4 enabled
            memory.Write32(adapter + 60, 1500);
            memory.Write32(adapter + 64, 6);                   // IF_TYPE_ETHERNET_CSMACD
            memory.Write32(adapter + 68, 1);                   // IfOperStatusUp
            memory.Write64(adapter + 144, 1_000_000_000);
            memory.Write64(adapter + 152, 1_000_000_000);
            memory.Write32(adapter + 168, 25);                 // Ipv4Metric
            return 0;
        }
    }
}
