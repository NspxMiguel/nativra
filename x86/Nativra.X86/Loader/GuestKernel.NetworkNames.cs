using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Nativra.X86.Loader
{
    // Names and addresses: inet_addr/inet_ntoa/inet_pton/inet_ntop, gethostbyname
    // and getaddrinfo with their guest-memory results, getnameinfo, and the
    // small protocol and service tables. A name that is not a literal is
    // resolved by the host in the background (HostResolver): the first call
    // starts the lookup and blocks the thread, which is asked again until the
    // lookup has ended; results are kept for a while. The strings, hostent and
    // servent a call returns are one block per guest thread, as Winsock keeps
    // them, so the guest must not free them and a later call overwrites them.
    public sealed partial class GuestKernel
    {
        private const uint WsaHostNotFound = 11001, WsaTryAgain = 11002, WsaNoData = 11004, WsaTypeNotFound = 10109;

        // AI_*, NI_* and the other flags.
        private const uint AiPassive = 0x1, AiCanonName = 0x2, AiNumericHost = 0x4, AiNumericServ = 0x8;
        private const uint NiNumericHost = 0x2, NiNameRequired = 0x4, NiNumericServ = 0x8;

        // One scratch block per guest thread.
        private const uint NetBlockSize = 0x200;
        private const uint HostentAliases = 0x10, HostentAddressList = 0x18, HostentAddresses = 0x40, HostentName = 0x60;
        private const uint NtoaText = 0x160, ProtoentAt = 0x170, ServentAt = 0x180, EntryText = 0x190;

        private readonly Dictionary<uint, uint> netBlocks = new Dictionary<uint, uint>();

        private uint NetBlock()
        {
            if (!netBlocks.TryGetValue(Me, out var block))
            {
                block = heap.Alloc(NetBlockSize, true);
                netBlocks[Me] = block;
            }
            return block;
        }

        /// <summary>
        /// How the host resolves a name that is not a literal address (the
        /// system resolver by default). A host that has its own way, or a test
        /// that wants to control when a lookup ends, replaces it.
        /// </summary>
        public Func<string, Task<IPAddress[]>> HostResolver { get; set; } = name => Dns.GetHostAddressesAsync(name);

        // --- literal addresses -----------------------------------------------------------------------------

        private static bool TryParseNumber(string text, out uint value)
        {
            value = 0;
            if (text.Length == 0) return false;
            var radix = 10;
            var start = 0;
            if (text.Length > 1 && text[0] == '0')
            {
                if (text[1] == 'x' || text[1] == 'X') { radix = 16; start = 2; }
                else { radix = 8; start = 1; }
            }
            if (start == text.Length) return radix == 8;   // "0" itself
            ulong total = 0;
            for (var n = start; n < text.Length; n++)
            {
                var ch = text[n];
                int digit;
                if (ch >= '0' && ch <= '9') digit = ch - '0';
                else if (radix == 16 && ch >= 'a' && ch <= 'f') digit = ch - 'a' + 10;
                else if (radix == 16 && ch >= 'A' && ch <= 'F') digit = ch - 'A' + 10;
                else return false;
                if (digit >= radix) return false;
                total = total * (uint)radix + (uint)digit;
                if (total > 0xFFFFFFFF) return false;
            }
            value = (uint)total;
            return true;
        }

        /// <summary>
        /// inet_addr's forms: a.b.c.d, a.b.c (the last part fills two bytes),
        /// a.b, and a; each part decimal, 0x hexadecimal or 0 octal.
        /// </summary>
        private static bool TryParseInetAddr(string text, out uint networkOrder)
        {
            networkOrder = 0;
            if (string.IsNullOrEmpty(text)) return false;
            var parts = text.Split('.');
            if (parts.Length > 4) return false;
            var values = new uint[parts.Length];
            for (var n = 0; n < parts.Length; n++)
                if (!TryParseNumber(parts[n], out values[n])) return false;
            uint host = 0;
            for (var n = 0; n < parts.Length - 1; n++)
            {
                if (values[n] > 0xFF) return false;
                host |= values[n] << (24 - 8 * n);
            }
            var last = values[parts.Length - 1];
            if ((ulong)last >= 1UL << (8 * (5 - parts.Length))) return false;
            networkOrder = SwapBytes(host | last);
            return true;
        }

        /// <summary>Exactly a.b.c.d, decimal, the way inet_pton wants it.</summary>
        private static bool TryParseDottedQuad(string text, out IPAddress address)
        {
            address = null;
            var parts = text.Split('.');
            if (parts.Length != 4) return false;
            var bytes = new byte[4];
            for (var n = 0; n < 4; n++)
            {
                var part = parts[n];
                if (part.Length == 0 || part.Length > 3) return false;
                var value = 0;
                foreach (var ch in part)
                {
                    if (ch < '0' || ch > '9') return false;
                    value = value * 10 + (ch - '0');
                }
                if (value > 255) return false;
                bytes[n] = (byte)value;
            }
            address = new IPAddress(bytes);
            return true;
        }

        private static bool TryParseIpv6(string text, out IPAddress address)
        {
            address = null;
            if (text.IndexOf(':') < 0 || text.IndexOf('%') >= 0 || text.IndexOf('[') >= 0 || text.IndexOf('/') >= 0) return false;
            return IPAddress.TryParse(text, out address) && address.AddressFamily == AddressFamily.InterNetworkV6;
        }

        private uint InetAddr(uint text) =>
            TryParseInetAddr(ReadText(text, false), out var address) ? address : SocketFailed;   // INADDR_NONE

        private uint InetNtoa(uint address)
        {
            var at = NetBlock() + NtoaText;
            WriteText(at, $"{address & 0xFF}.{(address >> 8) & 0xFF}.{(address >> 16) & 0xFF}.{address >> 24}", false);
            return at;
        }

        private uint InetPton(uint family, string text, uint destination)
        {
            if (family == 2)
            {
                if (!TryParseDottedQuad(text, out var v4)) return 0;
                memory.WriteBytes(destination, v4.GetAddressBytes());
                return 1;
            }
            if (family == 23)
            {
                if (!TryParseIpv6(text, out var v6)) return 0;
                memory.WriteBytes(destination, v6.GetAddressBytes());
                return 1;
            }
            return SockFail(WsaEafnosupport);
        }

        private uint InetNtop(uint family, uint source, uint destination, uint size, bool wide)
        {
            string text;
            if (family == 2) text = new IPAddress(memory.ReadBytes(source, 4)).ToString();
            else if (family == 23) text = new IPAddress(memory.ReadBytes(source, 16)).ToString();
            else { SetSocketError(WsaEafnosupport); return 0; }
            if (destination == 0 || size < text.Length + 1) { SetSocketError(WsaEfault); return 0; }
            WriteText(destination, text, wide);
            return destination;
        }

        // --- this machine ------------------------------------------------------------------------------------

        private uint GetHostName(uint buffer, uint length)
        {
            var name = HostName ?? "";
            if (buffer == 0 || length < name.Length + 1) return SockFail(WsaEfault);
            WriteText(buffer, name, false);
            return 0;
        }

        private IPAddress[] localAddresses;
        private long localAddressesAt;

        /// <summary>This machine's own IPv4 addresses (loopback when it has none), what its host name resolves to.</summary>
        private IPAddress[] LocalAddresses()
        {
            var now = clock.ElapsedMilliseconds;
            if (localAddresses != null && now - localAddressesAt < 30000) return localAddresses;
            var found = new List<IPAddress>();
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                        if (unicast.Address.AddressFamily == AddressFamily.InterNetwork && !found.Contains(unicast.Address))
                            found.Add(unicast.Address);
                }
            }
            catch (NetworkInformationException) { }
            catch (NotSupportedException) { }
            catch (UnauthorizedAccessException) { }
            if (found.Count == 0) found.Add(IPAddress.Loopback);
            localAddresses = found.ToArray();
            localAddressesAt = now;
            return localAddresses;
        }

        // --- resolving a name ---------------------------------------------------------------------------------

        private sealed class NameLookup
        {
            public Task<IPAddress[]> Task;
            public long Started, Finished;   // clock milliseconds; Finished is 0 while it runs
            public IPAddress[] Addresses;
            public uint Error;
        }

        private readonly Dictionary<string, NameLookup> lookups = new Dictionary<string, NameLookup>(StringComparer.OrdinalIgnoreCase);

        private const long LookupGiveUp = 15000, LookupKept = 5 * 60 * 1000, FailureKept = 5000;

        private static uint ResolveCode(Exception e)
        {
            var inner = e is AggregateException a ? a.InnerException : e;
            if (inner is SocketException se)
            {
                var code = (uint)se.SocketErrorCode;
                if (code == 11001 || code == 11002 || code == 11003 || code == 11004) return code;
            }
            return WsaHostNotFound;
        }

        /// <summary>
        /// Resolves a host name. False while the lookup runs (the caller
        /// waits); true when it has ended, with the addresses or the error.
        /// </summary>
        private bool ResolveName(string name, out IPAddress[] addresses, out uint error)
        {
            addresses = null;
            error = 0;
            if (TryParseDottedQuad(name, out var v4)) { addresses = new[] { v4 }; return true; }
            if (TryParseIpv6(name, out var v6)) { addresses = new[] { v6 }; return true; }

            var bare = name.TrimEnd('.');
            if (bare.Length == 0 || string.Equals(bare, HostName, StringComparison.OrdinalIgnoreCase))
            {
                addresses = LocalAddresses();
                return true;
            }
            if (string.Equals(bare, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                addresses = new[] { IPAddress.Loopback, IPAddress.IPv6Loopback };
                return true;
            }

            var now = clock.ElapsedMilliseconds;
            if (!lookups.TryGetValue(name, out var lookup) ||
                (lookup.Finished != 0 && now - lookup.Finished > (lookup.Error == 0 ? LookupKept : FailureKept)))
            {
                lookup = new NameLookup { Started = now };
                try
                {
                    lookup.Task = HostResolver(name);
                    if (lookup.Task == null) throw new InvalidOperationException("no resolver");
                    // A lookup nobody collects must not surface as an unobserved failure.
                    lookup.Task.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                }
                catch (Exception e) when (e is SocketException || e is ArgumentException || e is InvalidOperationException || e is NotSupportedException)
                {
                    lookup.Task = null;
                    lookup.Finished = now;
                    lookup.Error = ResolveCode(e);
                }
                lookups[name] = lookup;
            }

            if (lookup.Finished == 0)
            {
                if (lookup.Task.IsCompleted) Harvest(lookup, now);
                else if (now - lookup.Started > LookupGiveUp) { error = WsaTryAgain; return true; }
                else return false;
            }
            error = lookup.Error;
            addresses = lookup.Addresses;
            return true;
        }

        private static void Harvest(NameLookup lookup, long now)
        {
            lookup.Finished = now;
            if (lookup.Task.IsFaulted) lookup.Error = ResolveCode(lookup.Task.Exception);
            else if (lookup.Task.IsCanceled) lookup.Error = WsaTryAgain;
            else
            {
                lookup.Addresses = lookup.Task.Result;
                if (lookup.Addresses == null || lookup.Addresses.Length == 0) lookup.Error = WsaNoData;
            }
        }

        private uint NetError(uint error)
        {
            SetSocketError(error);
            return error;
        }

        // --- hostent -----------------------------------------------------------------------------------------

        private static List<IPAddress> V4(IEnumerable<IPAddress> addresses)
        {
            var list = new List<IPAddress>();
            foreach (var a in addresses)
                if (a.AddressFamily == AddressFamily.InterNetwork && !list.Contains(a)) list.Add(a);
            return list;
        }

        // struct hostent { char *h_name; char **h_aliases; short h_addrtype; short h_length; char **h_addr_list; }
        private uint WriteHostent(string name, List<IPAddress> addresses)
        {
            var block = NetBlock();
            memory.Write32(block + 0, block + HostentName);
            memory.Write32(block + 4, block + HostentAliases);
            memory.Write16(block + 8, 2);   // AF_INET
            memory.Write16(block + 10, 4);
            memory.Write32(block + 12, block + HostentAddressList);
            memory.Write32(block + HostentAliases, 0);
            var count = Math.Min(addresses.Count, 8);
            for (var n = 0; n < count; n++)
            {
                var at = block + HostentAddresses + (uint)n * 4;
                memory.WriteBytes(at, addresses[n].GetAddressBytes());
                memory.Write32(block + HostentAddressList + (uint)n * 4, at);
            }
            memory.Write32(block + HostentAddressList + (uint)count * 4, 0);
            WriteText(block + HostentName, name.Length > 250 ? name.Substring(0, 250) : name, false);
            return block;
        }

        private uint GetHostByName(uint namePointer)
        {
            var name = ReadText(namePointer, false);
            if (!ResolveName(name, out var addresses, out var error)) { process.BlockOnHost(); return 0; }
            if (error != 0) { SetSocketError(error); return 0; }
            var v4 = V4(addresses);
            if (v4.Count == 0) { SetSocketError(WsaNoData); return 0; }
            return WriteHostent(name.Length == 0 ? HostName : name, v4);
        }

        private uint GetHostByAddr(uint address, uint length, uint family)
        {
            if (address == 0) { SetSocketError(WsaEfault); return 0; }
            if (family != 2 || length != 4) { SetSocketError(family != 2 ? WsaEafnosupport : WsaEinval); return 0; }
            var ip = new IPAddress(memory.ReadBytes(address, 4));
            // Only this machine's own addresses have a name here; no reverse lookups are made.
            if (IPAddress.IsLoopback(ip)) return WriteHostent("localhost", new List<IPAddress> { ip });
            foreach (var local in LocalAddresses())
                if (local.Equals(ip)) return WriteHostent(HostName, new List<IPAddress> { ip });
            SetSocketError(WsaHostNotFound);
            return 0;
        }

        // --- protocols and services ----------------------------------------------------------------------------

        private static readonly string[] ProtocolNames = { "ip", "icmp", "tcp", "udp" };
        private static readonly uint[] ProtocolNumbers = { 0, 1, 6, 17 };

        private static readonly Dictionary<string, ushort> Services = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
        {
            ["echo"] = 7, ["discard"] = 9, ["daytime"] = 13, ["ftp-data"] = 20, ["ftp"] = 21, ["ssh"] = 22, ["telnet"] = 23,
            ["smtp"] = 25, ["time"] = 37, ["domain"] = 53, ["tftp"] = 69, ["gopher"] = 70, ["finger"] = 79, ["http"] = 80,
            ["kerberos"] = 88, ["pop3"] = 110, ["sunrpc"] = 111, ["ntp"] = 123, ["imap"] = 143, ["snmp"] = 161, ["ldap"] = 389,
            ["https"] = 443, ["microsoft-ds"] = 445, ["isakmp"] = 500, ["submission"] = 587, ["ldaps"] = 636, ["imaps"] = 993,
            ["pop3s"] = 995,
        };

        // struct protoent { char *p_name; char **p_aliases; short p_proto; }
        private uint WriteProtoent(string name, uint number)
        {
            var block = NetBlock();
            var text = block + EntryText;
            WriteText(text, name, false);
            var at = block + ProtoentAt;
            memory.Write32(at + 0, text);
            memory.Write32(at + 4, block + HostentAliases);   // an empty alias list
            memory.Write16(at + 8, (ushort)number);
            return at;
        }

        private uint GetProtoByName(uint name)
        {
            var wanted = ReadText(name, false);
            for (var n = 0; n < ProtocolNames.Length; n++)
                if (string.Equals(ProtocolNames[n], wanted, StringComparison.OrdinalIgnoreCase)) return WriteProtoent(ProtocolNames[n], ProtocolNumbers[n]);
            SetSocketError(WsaNoData);
            return 0;
        }

        private uint GetProtoByNumber(uint number)
        {
            for (var n = 0; n < ProtocolNumbers.Length; n++)
                if (ProtocolNumbers[n] == number) return WriteProtoent(ProtocolNames[n], number);
            SetSocketError(WsaNoData);
            return 0;
        }

        // struct servent { char *s_name; char **s_aliases; short s_port; char *s_proto; }
        private uint WriteServent(string name, ushort port, string protocol)
        {
            var block = NetBlock();
            var text = block + EntryText + 0x10;
            WriteText(text, name, false);
            var protoText = block + EntryText + 0x30;
            WriteText(protoText, protocol, false);
            var at = block + ServentAt;
            memory.Write32(at + 0, text);
            memory.Write32(at + 4, block + HostentAliases);
            memory.Write16(at + 8, (ushort)((port >> 8) | (port << 8)));   // in network order
            memory.Write32(at + 12, protoText);
            return at;
        }

        private static string ServiceProtocol(string given) =>
            string.Equals(given, "udp", StringComparison.OrdinalIgnoreCase) ? "udp" : "tcp";

        private uint GetServByName(uint name, uint protocol)
        {
            var wanted = ReadText(name, false);
            if (!Services.TryGetValue(wanted, out var port)) { SetSocketError(WsaNoData); return 0; }
            return WriteServent(wanted.ToLowerInvariant(), port, ServiceProtocol(protocol == 0 ? null : ReadText(protocol, false)));
        }

        private uint GetServByPort(uint networkPort, uint protocol)
        {
            var port = (ushort)(((networkPort & 0xFF) << 8) | ((networkPort >> 8) & 0xFF));
            foreach (var pair in Services)
                if (pair.Value == port)
                    return WriteServent(pair.Key, port, ServiceProtocol(protocol == 0 ? null : ReadText(protocol, false)));
            SetSocketError(WsaNoData);
            return 0;
        }

        // --- getaddrinfo -----------------------------------------------------------------------------------------

        /// <summary>A service as a port: a number, or (unless numeric only) a name from the small table.</summary>
        private static bool TryServicePort(string service, uint flags, out uint port)
        {
            port = 0;
            var digits = service.Length > 0;
            foreach (var ch in service) if (ch < '0' || ch > '9') digits = false;
            if (digits)
            {
                if (service.Length > 5 || !uint.TryParse(service, out port) || port > 65535) return false;
                return true;
            }
            if ((flags & AiNumericServ) != 0) return false;
            if (!Services.TryGetValue(service, out var known)) return false;
            port = known;
            return true;
        }

        // ADDRINFOA / ADDRINFOW (32-bit, 32 bytes): ai_flags, ai_family, ai_socktype, ai_protocol,
        // ai_addrlen, ai_canonname, ai_addr, ai_next. Each result is one block: the record, the
        // sockaddr after it, and for the first one a canonical name.
        private uint GetAddrInfo(uint nodePointer, uint servicePointer, uint hints, uint resultPointer, bool wide)
        {
            if (resultPointer == 0) return NetError(WsaEinval);
            memory.Write32(resultPointer, 0);
            var node = nodePointer == 0 ? null : ReadText(nodePointer, wide);
            var service = servicePointer == 0 ? null : ReadText(servicePointer, wide);
            if (string.IsNullOrEmpty(node)) node = null;
            if (string.IsNullOrEmpty(service)) service = null;

            uint flags = 0, family = 0, socketType = 0, protocol = 0;
            if (hints != 0)
            {
                flags = memory.Read32(hints);
                family = memory.Read32(hints + 4);
                socketType = memory.Read32(hints + 8);
                protocol = memory.Read32(hints + 12);
            }
            if (node == null && service == null) return NetError(WsaHostNotFound);
            if (family != 0 && family != 2 && family != 23) return NetError(WsaEafnosupport);
            if (socketType != 0 && socketType != 1 && socketType != 2) return NetError(WsaEsocktnosupport);
            if (protocol != 0 && protocol != 6 && protocol != 17) return NetError(WsaEprotonosupport);

            uint port = 0;
            if (service != null && !TryServicePort(service, flags, out port)) return NetError(WsaTypeNotFound);

            IPAddress[] addresses;
            if (node == null)
                addresses = (flags & AiPassive) != 0
                    ? new[] { IPAddress.Any, IPAddress.IPv6Any }
                    : new[] { IPAddress.Loopback, IPAddress.IPv6Loopback };
            else
            {
                if ((flags & AiNumericHost) != 0 && !TryParseDottedQuad(node, out _) && !TryParseIpv6(node, out _))
                    return NetError(WsaHostNotFound);
                if (!ResolveName(node, out addresses, out var error)) { process.BlockOnHost(); return 0; }
                if (error != 0) return NetError(error);
            }

            // IPv4 first, then IPv6, whatever order the resolver gave.
            var ordered = new List<IPAddress>();
            if (family != 23) foreach (var a in addresses) if (a.AddressFamily == AddressFamily.InterNetwork && !ordered.Contains(a)) ordered.Add(a);
            if (family != 2) foreach (var a in addresses) if (a.AddressFamily == AddressFamily.InterNetworkV6 && !ordered.Contains(a)) ordered.Add(a);
            if (ordered.Count == 0) return NetError(WsaHostNotFound);

            var kinds = new List<uint[]>();
            if ((socketType == 0 || socketType == 1) && (protocol == 0 || protocol == 6)) kinds.Add(new uint[] { 1, 6 });
            if ((socketType == 0 || socketType == 2) && (protocol == 0 || protocol == 17)) kinds.Add(new uint[] { 2, 17 });
            if (kinds.Count == 0) return NetError(WsaEsocktnosupport);

            var canonical = (flags & AiCanonName) != 0 ? (node ?? HostName) : null;
            uint first = 0, previous = 0;
            foreach (var address in ordered)
            {
                foreach (var kind in kinds)
                {
                    var sockaddr = SockaddrBytes(new IPEndPoint(address, (int)port));
                    var canonBytes = first == 0 && canonical != null ? (uint)(canonical.Length + 1) * (wide ? 2u : 1u) : 0u;
                    var entry = heap.Alloc(64 + canonBytes, true);
                    if (entry == 0) { if (first != 0) FreeAddrInfo(first); return NetError(8); }   // WSA_NOT_ENOUGH_MEMORY
                    memory.Write32(entry + 4, address.AddressFamily == AddressFamily.InterNetworkV6 ? 23u : 2u);
                    memory.Write32(entry + 8, kind[0]);
                    memory.Write32(entry + 12, kind[1]);
                    memory.Write32(entry + 16, (uint)sockaddr.Length);
                    memory.Write32(entry + 20, canonBytes > 0 ? entry + 64 : 0);
                    memory.Write32(entry + 24, entry + 32);
                    memory.WriteBytes(entry + 32, sockaddr);
                    if (canonBytes > 0) WriteText(entry + 64, canonical, wide);
                    if (previous != 0) memory.Write32(previous + 28, entry); else first = entry;
                    previous = entry;
                }
            }
            memory.Write32(resultPointer, first);
            return 0;
        }

        private void FreeAddrInfo(uint list)
        {
            for (var guard = 0; list != 0 && guard < 64; guard++)
            {
                var next = memory.Read32(list + 28);
                heap.Free(list);
                list = next;
            }
        }

        // --- getnameinfo -----------------------------------------------------------------------------------------

        private uint GetNameInfo(uint address, uint addressLength, uint host, uint hostLength, uint service, uint serviceLength, uint flags, bool wide)
        {
            var error = ReadSockaddr(address, addressLength, out var endpoint);
            if (error != 0) return NetError(error);
            if (host == 0 && service == 0) return NetError(WsaEinval);

            if (host != 0)
            {
                // Numeric only: no reverse lookups are made, so a name is never required of the answer.
                if ((flags & NiNameRequired) != 0) return NetError(WsaHostNotFound);
                var text = endpoint.Address.ToString();
                if (hostLength < text.Length + 1) return NetError(WsaEfault);
                WriteText(host, text, wide);
            }
            if (service != 0)
            {
                var port = (ushort)endpoint.Port;
                var text = port.ToString();
                if ((flags & NiNumericServ) == 0)
                    foreach (var pair in Services)
                        if (pair.Value == port) { text = pair.Key; break; }
                if (serviceLength < text.Length + 1) return NetError(WsaEfault);
                WriteText(service, text, wide);
            }
            return 0;
        }
    }
}
