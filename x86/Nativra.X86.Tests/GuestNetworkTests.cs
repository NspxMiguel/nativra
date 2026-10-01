using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Nativra.X86.Cpu;
using Nativra.X86.Loader;
using Xunit;

namespace Nativra.X86.Tests
{
    /// <summary>
    /// ws2_32 and wsock32 against real loopback sockets, driven through the
    /// guest API as a program would call it. Blocking calls are exercised too:
    /// the single guest thread is asked again by the scheduler until the call
    /// can answer, which is exactly what a blocking socket call does.
    /// </summary>
    public sealed class GuestNetworkTests : IDisposable
    {
        private const uint Invalid = 0xFFFFFFFF;                 // SOCKET_ERROR / INVALID_SOCKET
        private const uint WouldBlock = 10035, TimedOut = 10060, NotSock = 10038, MsgSize = 10040;
        private const uint SolSocket = 0xFFFF, SoReuseAddr = 4, SoBroadcast = 0x20, SoRcvBuf = 0x1002, SoRcvTimeo = 0x1006;
        private const uint SoError = 0x1007, SoType = 0x1008, SoLinger = 0x80;
        private const uint FdRead = 1, FdWrite = 2, FdAccept = 8, FdConnect = 0x10, FdClose = 0x20;

        private readonly GuestProcess p;
        private readonly GuestKernel k;

        public GuestNetworkTests()
        {
            p = new GuestProcess(new GuestMemory(), useJit: false);
            k = new GuestKernel(p);
            k.Install();
            Assert.Equal(0u, W("WSAStartup", 0x0202, Alloc(400)));
        }

        public void Dispose()
        {
            k.CloseNetwork();
            p.Dispose();
        }

        // --- helpers -------------------------------------------------------------------------------------

        private uint Call(string module, string function, params uint[] args)
        {
            var sentinel = p.Imports.Bind(module, function, -1);
            var result = p.Call(sentinel, out var eax, 1_000_000, args);
            Assert.True(result.Ok, $"{function} stopped as {result}");
            return eax;
        }

        private uint W(string function, params uint[] args) => Call("ws2_32.dll", function, args);
        private uint K(string function, params uint[] args) => Call("kernel32.dll", function, args);

        private uint Alloc(uint size) => k.Heap.Alloc(size, zero: true);

        private uint Ansi(string text)
        {
            var at = Alloc((uint)text.Length + 1);
            p.Memory.WriteAnsi(at, text);
            return at;
        }

        private uint Wide(string text)
        {
            var at = Alloc((uint)(text.Length + 1) * 2);
            p.Memory.WriteUnicode(at, text);
            return at;
        }

        private uint Dword(uint value)
        {
            var at = Alloc(4);
            p.Memory.Write32(at, value);
            return at;
        }

        private uint Addr(string ip, int port)
        {
            var at = Alloc(16);
            p.Memory.Write16(at, 2);
            p.Memory.Write8(at + 2, (byte)(port >> 8));
            p.Memory.Write8(at + 3, (byte)port);
            p.Memory.WriteBytes(at + 4, IPAddress.Parse(ip).GetAddressBytes());
            return at;
        }

        private int PortOf(uint sockaddr) => (p.Memory.Read8(sockaddr + 2) << 8) | p.Memory.Read8(sockaddr + 3);
        private string IpOf(uint sockaddr) => new IPAddress(p.Memory.ReadBytes(sockaddr + 4, 4)).ToString();

        private uint FdSet(params uint[] handles)
        {
            var at = Alloc(260);
            p.Memory.Write32(at, (uint)handles.Length);
            for (var n = 0; n < handles.Length; n++) p.Memory.Write32(at + 4 + (uint)n * 4, handles[n]);
            return at;
        }

        private uint[] Members(uint set)
        {
            var count = p.Memory.Read32(set);
            var list = new uint[count];
            for (uint n = 0; n < count; n++) list[n] = p.Memory.Read32(set + 4 + n * 4);
            return list;
        }

        private uint Timeval(int milliseconds)
        {
            var at = Alloc(8);
            p.Memory.Write32(at, (uint)(milliseconds / 1000));
            p.Memory.Write32(at + 4, (uint)(milliseconds % 1000 * 1000));
            return at;
        }

        private uint Last() => W("WSAGetLastError");

        private void NonBlocking(uint socket) => Assert.Equal(0u, W("ioctlsocket", socket, 0x8004667E, Dword(1)));

        private uint Socket(uint type, uint protocol)
        {
            var s = W("socket", 2, type, protocol);
            Assert.NotEqual(Invalid, s);
            return s;
        }

        /// <summary>A UDP socket bound to a loopback port the system chose.</summary>
        private uint BoundUdp(out int port)
        {
            var s = Socket(2, 17);
            Assert.Equal(0u, W("bind", s, Addr("127.0.0.1", 0), 16));
            port = PortOfSocket(s);
            return s;
        }

        private int PortOfSocket(uint socket)
        {
            var name = Alloc(28);
            var length = Dword(28);
            Assert.Equal(0u, W("getsockname", socket, name, length));
            return PortOf(name);
        }

        private void Send(uint socket, string text, uint to = 0)
        {
            Assert.Equal((uint)text.Length, to == 0 ? W("send", socket, Ansi(text), (uint)text.Length, 0)
                                                    : W("sendto", socket, Ansi(text), (uint)text.Length, 0, to, 16));
        }

        private string Receive(uint socket, uint capacity = 256)
        {
            var buffer = Alloc(capacity);
            var n = W("recv", socket, buffer, capacity, 0);
            Assert.NotEqual(Invalid, n);
            return p.Memory.ReadAnsi(buffer, (int)n).Substring(0, (int)n);
        }

        /// <summary>Waits (through select) until a socket is readable.</summary>
        private void AwaitReadable(uint socket)
        {
            var set = FdSet(socket);
            Assert.Equal(1u, W("select", 0, set, 0, 0, Timeval(3000)));
            Assert.Equal(new[] { socket }, Members(set));
        }

        // --- the required round trips -----------------------------------------------------------------------

        [Fact]
        public void UdpSocketSendsToItselfAndSelectAndRecvfromReadItBack()
        {
            var s = BoundUdp(out var port);
            Assert.NotEqual(0, port);
            NonBlocking(s);

            // Nothing has arrived: a non-blocking receive says so, and select with no time to wait finds nothing.
            var buffer = Alloc(256);
            Assert.Equal(Invalid, W("recvfrom", s, buffer, 256, 0, 0, 0));
            Assert.Equal(WouldBlock, Last());
            var idle = FdSet(s);
            Assert.Equal(0u, W("select", 0, idle, 0, 0, Timeval(0)));
            Assert.Empty(Members(idle));   // the set is rewritten to the ready sockets: none

            Assert.Equal(13u, W("sendto", s, Ansi("hello nativra"), 13, 0, Addr("127.0.0.1", port), 16));
            AwaitReadable(s);

            var from = Alloc(16);
            var fromLength = Dword(16);
            Assert.Equal(13u, W("recvfrom", s, buffer, 256, 0, from, fromLength));
            Assert.Equal("hello nativra", p.Memory.ReadAnsi(buffer));
            Assert.Equal(16u, p.Memory.Read32(fromLength));
            Assert.Equal(2u, p.Memory.Read16(from));
            Assert.Equal(port, PortOf(from));
            Assert.Equal("127.0.0.1", IpOf(from));
            Assert.Equal(0u, W("closesocket", s));
        }

        [Fact]
        public void TcpListenConnectAcceptSendAndReceiveRoundTrip()
        {
            var listener = Socket(1, 6);
            Assert.Equal(0u, W("bind", listener, Addr("127.0.0.1", 0), 16));
            Assert.Equal(0u, W("listen", listener, 5));
            var port = PortOfSocket(listener);
            NonBlocking(listener);

            var client = Socket(1, 6);
            NonBlocking(client);
            Assert.Equal(Invalid, W("connect", client, Addr("127.0.0.1", port), 16));
            Assert.Equal(WouldBlock, Last());

            // Connected is writable; nothing went wrong.
            var writable = FdSet(client);
            Assert.Equal(1u, W("select", 0, 0, writable, 0, Timeval(3000)));
            var errorOption = Dword(99);
            var errorLength = Dword(4);
            Assert.Equal(0u, W("getsockopt", client, SolSocket, SoError, errorOption, errorLength));
            Assert.Equal(0u, p.Memory.Read32(errorOption));
            Assert.Equal(Invalid, W("connect", client, Addr("127.0.0.1", port), 16));   // already connected
            Assert.Equal(10056u, Last());

            AwaitReadable(listener);
            var peer = Alloc(16);
            var peerLength = Dword(16);
            var server = W("accept", listener, peer, peerLength);
            Assert.NotEqual(Invalid, server);
            Assert.Equal("127.0.0.1", IpOf(peer));
            Assert.Equal(Invalid, W("accept", listener, 0, 0));   // none left
            Assert.Equal(WouldBlock, Last());

            // Both ends know each other.
            var name = Alloc(16);
            var nameLength = Dword(16);
            Assert.Equal(0u, W("getpeername", client, name, nameLength));
            Assert.Equal(port, PortOf(name));
            Assert.Equal(0u, W("getsockname", server, name, nameLength));
            Assert.Equal(port, PortOf(name));

            Send(client, "ping");
            AwaitReadable(server);
            Assert.Equal("ping", Receive(server));
            Send(server, "pong, and more");
            AwaitReadable(client);
            Assert.Equal("pong, and more", Receive(client));

            // The accepted socket is non-blocking too (it has the listening socket's properties).
            Assert.Equal(Invalid, W("recv", server, Alloc(8), 8, 0));
            Assert.Equal(WouldBlock, Last());

            // A graceful close reads as zero bytes.
            Assert.Equal(0u, W("shutdown", client, 1));
            Assert.Equal(Invalid, W("send", client, Ansi("x"), 1, 0));
            Assert.Equal(10058u, Last());   // WSAESHUTDOWN
            AwaitReadable(server);
            Assert.Equal(0u, W("recv", server, Alloc(8), 8, 0));
            Assert.Equal(0u, W("closesocket", server));
            Assert.Equal(0u, W("closesocket", client));
            Assert.Equal(0u, W("closesocket", listener));
        }

        [Fact]
        public void GetAddrInfoResolvesALiteralAddress()
        {
            var result = Alloc(4);
            Assert.Equal(0u, W("getaddrinfo", Ansi("127.0.0.1"), Ansi("80"), 0, result));
            var info = p.Memory.Read32(result);
            Assert.NotEqual(0u, info);
            Assert.Equal(2u, p.Memory.Read32(info + 4));      // ai_family AF_INET
            Assert.Equal(1u, p.Memory.Read32(info + 8));      // SOCK_STREAM first
            Assert.Equal(6u, p.Memory.Read32(info + 12));     // IPPROTO_TCP
            Assert.Equal(16u, p.Memory.Read32(info + 16));    // ai_addrlen
            var address = p.Memory.Read32(info + 24);
            Assert.Equal(2u, p.Memory.Read16(address));
            Assert.Equal(80, PortOf(address));
            Assert.Equal("127.0.0.1", IpOf(address));
            var next = p.Memory.Read32(info + 28);
            Assert.NotEqual(0u, next);                        // and the datagram entry after it
            Assert.Equal(2u, p.Memory.Read32(next + 8));
            Assert.Equal(17u, p.Memory.Read32(next + 12));
            Assert.Equal(0u, p.Memory.Read32(next + 28));
            W("freeaddrinfo", info);

            // Hints narrow it to one entry; a service name is a port.
            var hints = Alloc(32);
            p.Memory.Write32(hints + 4, 2);
            p.Memory.Write32(hints + 8, 2);
            Assert.Equal(0u, W("getaddrinfo", Ansi("127.0.0.1"), Ansi("http"), hints, result));
            info = p.Memory.Read32(result);
            Assert.Equal(2u, p.Memory.Read32(info + 8));
            Assert.Equal(17u, p.Memory.Read32(info + 12));
            Assert.Equal(0u, p.Memory.Read32(info + 28));
            Assert.Equal(80, PortOf(p.Memory.Read32(info + 24)));
            W("freeaddrinfo", info);

            // Not a number, and told so: no lookup is made.
            p.Memory.Write32(hints, 4);   // AI_NUMERICHOST
            Assert.Equal(11001u, W("getaddrinfo", Ansi("not.an.address"), 0, hints, result));
            Assert.Equal(0u, p.Memory.Read32(result));
            Assert.Equal(11001u, Last());
        }

        // --- getaddrinfo and friends in more detail ---------------------------------------------------------

        [Fact]
        public void GetAddrInfoPassiveWideAndCanonicalName()
        {
            var result = Alloc(4);
            var hints = Alloc(32);
            p.Memory.Write32(hints, 1);   // AI_PASSIVE
            p.Memory.Write32(hints + 4, 2);
            p.Memory.Write32(hints + 8, 2);
            Assert.Equal(0u, W("getaddrinfo", 0, Ansi("27015"), hints, result));
            var info = p.Memory.Read32(result);
            Assert.Equal("0.0.0.0", IpOf(p.Memory.Read32(info + 24)));   // INADDR_ANY
            Assert.Equal(27015, PortOf(p.Memory.Read32(info + 24)));
            W("freeaddrinfo", info);

            // Without AI_PASSIVE the null node is the loopback address.
            p.Memory.Write32(hints, 0);
            Assert.Equal(0u, W("getaddrinfo", 0, Ansi("1"), hints, result));
            info = p.Memory.Read32(result);
            Assert.Equal("127.0.0.1", IpOf(p.Memory.Read32(info + 24)));
            W("freeaddrinfo", info);

            // The wide form, with a canonical name.
            p.Memory.Write32(hints, 2);   // AI_CANONNAME
            Assert.Equal(0u, W("GetAddrInfoW", Wide("127.0.0.1"), Wide("8080"), hints, result));
            info = p.Memory.Read32(result);
            Assert.Equal("127.0.0.1", p.Memory.ReadUnicode(p.Memory.Read32(info + 20)));
            Assert.Equal(8080, PortOf(p.Memory.Read32(info + 24)));
            W("FreeAddrInfoW", info);

            // Neither node nor service; an unknown service.
            Assert.Equal(11001u, W("getaddrinfo", 0, 0, 0, result));
            Assert.Equal(10109u, W("getaddrinfo", Ansi("127.0.0.1"), Ansi("no-such-service"), 0, result));
        }

        [Fact]
        public void GetNameInfoIsNumeric()
        {
            var host = Alloc(64);
            var service = Alloc(32);
            Assert.Equal(0u, W("getnameinfo", Addr("10.1.2.3", 443), 16, host, 64, service, 32, 0x2 | 0x8));
            Assert.Equal("10.1.2.3", p.Memory.ReadAnsi(host));
            Assert.Equal("443", p.Memory.ReadAnsi(service));
            Assert.Equal(0u, W("getnameinfo", Addr("10.1.2.3", 443), 16, 0, 0, service, 32, 0));
            Assert.Equal("https", p.Memory.ReadAnsi(service));   // a well-known port by name unless numeric is asked
            Assert.NotEqual(0u, W("getnameinfo", Addr("10.1.2.3", 443), 16, host, 4, 0, 0, 0));   // too small
        }

        [Fact]
        public void InetFunctionsConvertAddresses()
        {
            Assert.Equal(0x0100007Fu, W("inet_addr", Ansi("127.0.0.1")));
            Assert.Equal(0x0100007Fu, W("inet_addr", Ansi("127.1")));          // the short forms
            Assert.Equal(0x0201A8C0u, W("inet_addr", Ansi("192.168.1.2")));
            Assert.Equal(0x0100007Fu, W("inet_addr", Ansi("0x7f.0.0.1")));
            Assert.Equal(Invalid, W("inet_addr", Ansi("256.1.1.1")));
            Assert.Equal(Invalid, W("inet_addr", Ansi("not an address")));
            Assert.Equal(Invalid, W("inet_addr", Ansi("1.2.3.4.5")));

            var text = W("inet_ntoa", 0x0201A8C0);
            Assert.Equal("192.168.1.2", p.Memory.ReadAnsi(text));

            var buffer = Alloc(16);
            Assert.Equal(1u, W("inet_pton", 2, Ansi("10.20.30.40"), buffer));
            Assert.Equal(new byte[] { 10, 20, 30, 40 }, p.Memory.ReadBytes(buffer, 4));
            Assert.Equal(0u, W("inet_pton", 2, Ansi("10.20.30"), buffer));      // inet_pton wants all four
            Assert.Equal(0u, W("inet_pton", 2, Ansi("10.20.30.400"), buffer));
            Assert.Equal(1u, W("inet_pton", 23, Ansi("::1"), buffer));
            Assert.Equal(1, p.Memory.Read8(buffer + 15));
            Assert.Equal(Invalid, W("inet_pton", 99, Ansi("::1"), buffer));
            Assert.Equal(10047u, Last());

            var output = Alloc(64);
            Assert.Equal(output, W("inet_ntop", 2, Dword(0x0100007F), output, 64));
            Assert.Equal("127.0.0.1", p.Memory.ReadAnsi(output));
            Assert.Equal(0u, W("inet_ntop", 2, Dword(0x0100007F), output, 4));   // too small
            Assert.Equal(0x3412u, W("htons", 0x1234));
            Assert.Equal(0x78563412u, W("htonl", 0x12345678));
            Assert.Equal(0x1234u, W("ntohs", 0x3412));
        }

        [Fact]
        public void GetHostByNameAnswersLocalNamesAtOnceAndThisMachineByItsHostName()
        {
            var entry = W("gethostbyname", Ansi("localhost"));
            Assert.NotEqual(0u, entry);
            Assert.Equal(2, p.Memory.Read16(entry + 8));    // h_addrtype
            Assert.Equal(4, p.Memory.Read16(entry + 10));   // h_length
            var list = p.Memory.Read32(entry + 12);
            var first = p.Memory.Read32(list);
            Assert.Equal(new byte[] { 127, 0, 0, 1 }, p.Memory.ReadBytes(first, 4));
            Assert.Equal(0u, p.Memory.Read32(list + 4));    // one address, then NULL
            Assert.Equal("localhost", p.Memory.ReadAnsi(p.Memory.Read32(entry)));

            // An IP literal.
            entry = W("gethostbyname", Ansi("10.9.8.7"));
            Assert.Equal(new byte[] { 10, 9, 8, 7 }, p.Memory.ReadBytes(p.Memory.Read32(p.Memory.Read32(entry + 12)), 4));

            // gethostname is what gethostbyname resolves to this machine.
            var name = Alloc(64);
            Assert.Equal(0u, W("gethostname", name, 64));
            Assert.Equal("xbox", p.Memory.ReadAnsi(name));
            Assert.Equal(Invalid, W("gethostname", name, 3));   // too small
            entry = W("gethostbyname", name);
            Assert.NotEqual(0u, entry);
            Assert.NotEqual(0u, p.Memory.Read32(p.Memory.Read32(entry + 12)));

            // The address back to a name, for this machine's own.
            entry = W("gethostbyaddr", Dword(0x0100007F), 4, 2);
            Assert.Equal("localhost", p.Memory.ReadAnsi(p.Memory.Read32(entry)));
            Assert.Equal(0u, W("gethostbyaddr", Dword(0x08080808), 4, 2));
            Assert.Equal(11001u, Last());
        }

        [Fact]
        public void GetHostByNameResolvesAnOtherNameInTheBackgroundAndCachesIt()
        {
            var asked = 0;
            var gate = new TaskCompletionSource<IPAddress[]>();
            k.HostResolver = name => { asked++; return gate.Task; };
            Task.Delay(150).ContinueWith(_ => gate.SetResult(new[] { IPAddress.Parse("203.0.113.9"), IPAddress.IPv6Loopback }));
            var watch = Stopwatch.StartNew();
            p.DeadlockMilliseconds = 20;   // the wait is the host's to end: it is not a deadlock

            var entry = W("gethostbyname", Ansi("game.example.test"));
            Assert.True(watch.ElapsedMilliseconds >= 120, "the call returned before the lookup ended");
            Assert.NotEqual(0u, entry);
            Assert.Equal(new byte[] { 203, 0, 113, 9 }, p.Memory.ReadBytes(p.Memory.Read32(p.Memory.Read32(entry + 12)), 4));
            Assert.Equal(0u, p.Memory.Read32(p.Memory.Read32(entry + 12) + 4));   // the IPv6 address is not a hostent's

            // Asked again, from the cache: no second lookup.
            Assert.NotEqual(0u, W("gethostbyname", Ansi("GAME.example.test")));
            Assert.Equal(1, asked);

            // getaddrinfo shares it, and lists IPv4 before IPv6.
            var result = Alloc(4);
            Assert.Equal(0u, W("getaddrinfo", Ansi("game.example.test"), 0, 0, result));
            var info = p.Memory.Read32(result);
            Assert.Equal(2u, p.Memory.Read32(info + 4));
            Assert.Equal("203.0.113.9", IpOf(p.Memory.Read32(info + 24)));
            var v6 = p.Memory.Read32(p.Memory.Read32(info + 28) + 28);   // after the TCP and UDP entries of the IPv4 address
            Assert.Equal(23u, p.Memory.Read32(v6 + 4));
            Assert.Equal(1, asked);
        }

        [Fact]
        public void AFailedLookupIsHostNotFound()
        {
            k.HostResolver = name => Task.FromException<IPAddress[]>(new SocketException((int)SocketError.HostNotFound));
            Assert.Equal(0u, W("gethostbyname", Ansi("nowhere.example.test")));
            Assert.Equal(11001u, Last());
            Assert.Equal(11001u, W("getaddrinfo", Ansi("nowhere.example.test"), 0, 0, Alloc(4)));
        }

        // --- blocking, timeouts and the scheduler ------------------------------------------------------------

        [Fact]
        public void BlockingReceiveTimesOutWithTheSocketsOwnTimeout()
        {
            var s = BoundUdp(out _);
            Assert.Equal(0u, W("setsockopt", s, SolSocket, SoRcvTimeo, Dword(120), 4));
            var watch = Stopwatch.StartNew();
            Assert.Equal(Invalid, W("recvfrom", s, Alloc(16), 16, 0, 0, 0));
            Assert.Equal(TimedOut, Last());
            Assert.InRange(watch.ElapsedMilliseconds, 100, 5000);
        }

        [Fact]
        public void BlockingReceiveWaitsForADatagramTheHostSendsLater()
        {
            var s = BoundUdp(out var port);
            p.DeadlockMilliseconds = 30;   // waiting for the network is not a deadlock, however long it takes
            Task.Delay(250).ContinueWith(_ =>
            {
                using (var sender = new UdpClient())
                    sender.Send(new byte[] { 1, 2, 3, 4, 5 }, 5, new IPEndPoint(IPAddress.Loopback, port));
            });
            var watch = Stopwatch.StartNew();
            var buffer = Alloc(16);
            Assert.Equal(5u, W("recv", s, buffer, 16, 0));
            Assert.True(watch.ElapsedMilliseconds >= 200);
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, p.Memory.ReadBytes(buffer, 5));
        }

        [Fact]
        public async Task BlockingConnectAcceptSendAndReceiveOnOneThread()
        {
            var listener = Socket(1, 6);
            Assert.Equal(0u, W("bind", listener, Addr("127.0.0.1", 0), 16));
            Assert.Equal(0u, W("listen", listener, 5));
            var port = PortOfSocket(listener);

            // A blocking connect waits for the handshake, which the system completes without accept.
            var client = Socket(1, 6);
            Assert.Equal(0u, W("connect", client, Addr("127.0.0.1", port), 16));
            Assert.Equal(Invalid, W("connect", client, Addr("127.0.0.1", port), 16));
            Assert.Equal(10056u, Last());   // WSAEISCONN
            var server = W("accept", listener, 0, 0);   // blocking, and a connection is waiting
            Assert.NotEqual(Invalid, server);

            // Blocking sends and receives both ways.
            Send(client, "first");
            Assert.Equal("first", Receive(server));
            Send(server, "second");
            Assert.Equal("second", Receive(client));

            // A blocking accept waits for a connection a host client makes later.
            p.DeadlockMilliseconds = 30;
            var again = Task.Delay(120).ContinueWith(_ => { using (var host = new TcpClient()) host.Connect(IPAddress.Loopback, port); });
            var late = W("accept", listener, 0, 0);
            Assert.NotEqual(Invalid, late);
            await again;

            Assert.Equal(0u, W("closesocket", late));
            Assert.Equal(0u, W("closesocket", server));
            Assert.Equal(0u, W("closesocket", client));
            Assert.Equal(0u, W("closesocket", listener));
        }

        [Fact]
        public async Task ABlockingSendOfALargeBufferWaitsForTheReceiverToDrainIt()
        {
            // The program sends 4 MB, more than the buffers of both ends hold, so the send must wait for the
            // reader; a host thread plays the server.
            var server = new TcpListener(IPAddress.Loopback, 0);
            server.Start();
            var port = ((IPEndPoint)server.LocalEndpoint).Port;
            var drained = Task.Run(() =>
            {
                long total = 0;
                uint sum = 0;
                using (var accepted = server.AcceptTcpClient())
                {
                    var buffer = new byte[65536];
                    int n;
                    var stream = accepted.GetStream();
                    while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        total += n;
                        for (var i = 0; i < n; i++) sum = sum * 31 + buffer[i];
                    }
                }
                server.Stop();
                return Tuple.Create(total, sum);
            });

            var payload = new byte[4_000_000];
            uint expected = 0;
            for (var n = 0; n < payload.Length; n++)
            {
                payload[n] = (byte)(n * 7 + n / 251);
                expected = expected * 31 + payload[n];
            }
            var source = Alloc((uint)payload.Length);
            p.Memory.WriteBytes(source, payload);

            var client = Socket(1, 6);
            Assert.Equal(0u, W("connect", client, Addr("127.0.0.1", port), 16));
            Assert.Equal((uint)payload.Length, W("send", client, source, (uint)payload.Length, 0));
            Assert.Equal(0u, W("closesocket", client));

            Assert.Same(drained, await Task.WhenAny(drained, Task.Delay(30000)));   // the reader saw the end
            var seen = await drained;
            Assert.Equal(payload.Length, seen.Item1);
            Assert.Equal(expected, seen.Item2);
        }

        [Fact]
        public void ANonBlockingSendOfALargeBufferTakesWhatFitsAndThenWouldBlock()
        {
            var server = new TcpListener(IPAddress.Loopback, 0);
            server.Start();
            var port = ((IPEndPoint)server.LocalEndpoint).Port;
            var client = Socket(1, 6);
            Assert.Equal(0u, W("connect", client, Addr("127.0.0.1", port), 16));
            using (var accepted = server.AcceptTcpClient())   // accepted and never read
            {
                NonBlocking(client);
                var big = Alloc(1_000_000);
                uint total = 0;
                for (var round = 0; round < 200; round++)
                {
                    var n = W("send", client, big, 1_000_000, 0);
                    if (n == Invalid) { Assert.Equal(WouldBlock, Last()); break; }
                    Assert.InRange(n, 1u, 1_000_000u);
                    total += n;
                }
                Assert.True(total > 0);
                Assert.Equal(Invalid, W("send", client, big, 1_000_000, 0));
                Assert.Equal(WouldBlock, Last());
                Assert.Equal(0u, W("closesocket", client));
            }
            server.Stop();
        }

        [Fact]
        public void SelectWithATimeoutWaitsAndThenEmptiesTheSets()
        {
            var s = BoundUdp(out _);
            var set = FdSet(s);
            var watch = Stopwatch.StartNew();
            Assert.Equal(0u, W("select", 0, set, 0, 0, Timeval(120)));
            Assert.InRange(watch.ElapsedMilliseconds, 100, 5000);
            Assert.Empty(Members(set));

            // A zero timeout only looks.
            watch.Restart();
            set = FdSet(s);
            Assert.Equal(0u, W("select", 0, set, 0, 0, Timeval(0)));
            Assert.True(watch.ElapsedMilliseconds < 100);

            // A datagram socket is writable at once; the same socket in both sets counts twice.
            var read = FdSet(s);
            var write = FdSet(s);
            Assert.Equal(1u, W("select", 0, read, write, 0, Timeval(0)));
            Assert.Empty(Members(read));
            Assert.Equal(new[] { s }, Members(write));
        }

        [Fact]
        public void SelectWithNothingToWaitOnOrAnUnknownSocketFails()
        {
            Assert.Equal(Invalid, W("select", 0, 0, 0, 0, Timeval(10)));
            Assert.Equal(10022u, Last());
            Assert.Equal(Invalid, W("select", 0, FdSet(0x7777), 0, 0, Timeval(10)));
            Assert.Equal(NotSock, Last());
        }

        [Fact]
        public void FdIsSetFindsAMemberOfTheSet()
        {
            var set = FdSet(0x104, 0x108);
            Assert.Equal(1u, W("__WSAFDIsSet", 0x108, set));
            Assert.Equal(0u, W("__WSAFDIsSet", 0x10C, set));
        }

        [Fact]
        public void SocketsNeedWsaStartupAndWsaCleanupClosesThem()
        {
            var s = Socket(2, 17);
            Assert.Equal(0u, W("WSACleanup"));   // the start-up of the constructor was the only one
            Assert.Equal(Invalid, W("socket", 2, 2, 17));
            Assert.Equal(10093u, Last());       // WSANOTINITIALISED
            Assert.Equal(Invalid, W("WSACleanup"));
            Assert.Equal(0u, W("WSAStartup", 0x0101, Alloc(400)));
            Assert.Equal(Invalid, W("closesocket", s));   // closed by the cleanup
            Assert.Equal(NotSock, Last());
        }

        [Fact]
        public void ClosedAndUnknownHandlesAreNotSockets()
        {
            var s = Socket(2, 17);
            Assert.Equal(0u, W("closesocket", s));
            Assert.Equal(Invalid, W("closesocket", s));
            Assert.Equal(NotSock, Last());
            Assert.Equal(Invalid, W("recv", s, Alloc(8), 8, 0));
            Assert.Equal(NotSock, Last());
            Assert.Equal(Invalid, W("send", 0x4444, Ansi("x"), 1, 0));
            Assert.Equal(NotSock, Last());

            // GetLastError is the same slot.
            Assert.Equal(NotSock, K("GetLastError"));
        }

        [Fact]
        public void CloseHandleClosesASocketToo()
        {
            var s = Socket(2, 17);
            Assert.Equal(1u, K("CloseHandle", s));
            Assert.Equal(Invalid, W("closesocket", s));
        }

        // --- creating, binding and options ---------------------------------------------------------------------

        [Fact]
        public void SocketCreationChecksItsArguments()
        {
            Assert.Equal(Invalid, W("socket", 99, 1, 6));
            Assert.Equal(10047u, Last());   // WSAEAFNOSUPPORT
            Assert.Equal(Invalid, W("socket", 2, 1, 17));
            Assert.Equal(10041u, Last());   // WSAEPROTOTYPE: a stream over UDP
            Assert.Equal(Invalid, W("socket", 2, 3, 0));
            Assert.Equal(10044u, Last());   // WSAESOCKTNOSUPPORT: raw sockets are not offered
            Assert.Equal(Invalid, W("socket", 2, 2, 99));
            Assert.Equal(10043u, Last());   // WSAEPROTONOSUPPORT

            // WSASocket takes the type from the protocol when it is 0.
            var s = W("WSASocketA", 2, 0, 17, 0, 0, 1);
            Assert.NotEqual(Invalid, s);
            var type = Dword(0);
            Assert.Equal(0u, W("getsockopt", s, SolSocket, SoType, type, Dword(4)));
            Assert.Equal(2u, p.Memory.Read32(type));   // SOCK_DGRAM
        }

        [Fact]
        public void BindingTheSamePortTwiceFailsAndAnAddressMustMatchTheFamily()
        {
            var first = BoundUdp(out var port);
            var second = Socket(2, 17);
            Assert.Equal(Invalid, W("bind", second, Addr("127.0.0.1", port), 16));
            Assert.Equal(10048u, Last());   // WSAEADDRINUSE
            Assert.Equal(Invalid, W("bind", second, Addr("127.0.0.1", 0), 8));
            Assert.Equal(10014u, Last());   // WSAEFAULT: the name is too short
            Assert.Equal(0u, W("bind", second, Addr("127.0.0.1", 0), 16));
            Assert.Equal(Invalid, W("bind", second, Addr("127.0.0.1", 0), 16));
            Assert.Equal(10022u, Last());   // WSAEINVAL: already bound
            Assert.Equal(0u, W("closesocket", first));
        }

        [Fact]
        public void SocketOptionsRoundTripAndUnknownOnesAreRefused()
        {
            var udp = Socket(2, 17);
            var value = Dword(0);
            var length = Dword(4);

            Assert.Equal(0u, W("setsockopt", udp, SolSocket, SoRcvBuf, Dword(32768), 4));
            Assert.Equal(0u, W("getsockopt", udp, SolSocket, SoRcvBuf, value, length));
            Assert.Equal(32768u, p.Memory.Read32(value));
            Assert.Equal(4u, p.Memory.Read32(length));

            Assert.Equal(0u, W("setsockopt", udp, SolSocket, SoBroadcast, Dword(1), 4));
            Assert.Equal(0u, W("getsockopt", udp, SolSocket, SoBroadcast, value, length));
            Assert.NotEqual(0u, p.Memory.Read32(value));
            Assert.Equal(0u, W("setsockopt", udp, SolSocket, SoReuseAddr, Dword(1), 4));

            Assert.Equal(0u, W("setsockopt", udp, SolSocket, SoRcvTimeo, Dword(1500), 4));
            Assert.Equal(0u, W("getsockopt", udp, SolSocket, SoRcvTimeo, value, length));
            Assert.Equal(1500u, p.Memory.Read32(value));

            Assert.Equal(0u, W("getsockopt", udp, SolSocket, SoType, value, length));
            Assert.Equal(2u, p.Memory.Read32(value));
            Assert.Equal(0u, W("getsockopt", udp, SolSocket, SoError, value, length));
            Assert.Equal(0u, p.Memory.Read32(value));

            // struct linger { u_short l_onoff; u_short l_linger; }
            var linger = Alloc(4);
            p.Memory.Write16(linger, 1);
            p.Memory.Write16(linger + 2, 5);
            var tcp = Socket(1, 6);
            Assert.Equal(0u, W("setsockopt", tcp, SolSocket, SoLinger, linger, 4));
            var back = Alloc(4);
            Assert.Equal(0u, W("getsockopt", tcp, SolSocket, SoLinger, back, Dword(4)));
            Assert.Equal(1, p.Memory.Read16(back));
            Assert.Equal(5, p.Memory.Read16(back + 2));

            Assert.Equal(0u, W("setsockopt", tcp, SolSocket, 8, Dword(1), 4));   // SO_KEEPALIVE
            Assert.Equal(0u, W("getsockopt", tcp, SolSocket, 8, value, length));
            Assert.NotEqual(0u, p.Memory.Read32(value));
            Assert.Equal(0u, W("setsockopt", tcp, 6, 1, Dword(1), 4));   // IPPROTO_TCP, TCP_NODELAY
            Assert.Equal(0u, W("getsockopt", tcp, 6, 1, value, length));
            Assert.NotEqual(0u, p.Memory.Read32(value));
            Assert.Equal(0u, W("setsockopt", udp, 0, 4, Dword(32), 4));   // IPPROTO_IP, IP_TTL
            Assert.Equal(0u, W("getsockopt", udp, 0, 4, value, length));
            Assert.Equal(32u, p.Memory.Read32(value));

            // Unknown options, and a buffer that is too short.
            Assert.Equal(Invalid, W("setsockopt", udp, SolSocket, 0x7777, Dword(1), 4));
            Assert.Equal(10042u, Last());   // WSAENOPROTOOPT
            Assert.Equal(Invalid, W("getsockopt", udp, SolSocket, 0x7777, value, length));
            Assert.Equal(10042u, Last());
            Assert.Equal(Invalid, W("setsockopt", udp, 6, 0x7777, Dword(1), 4));
            Assert.Equal(10042u, Last());
            p.Memory.Write32(length, 2);
            Assert.Equal(Invalid, W("getsockopt", udp, SolSocket, SoType, value, length));
            Assert.Equal(10014u, Last());   // WSAEFAULT
        }

        [Fact]
        public void IoctlSocketTogglesBlockingModeAndCountsPendingBytes()
        {
            var s = BoundUdp(out var port);
            var arg = Dword(0);
            Assert.Equal(0u, W("ioctlsocket", s, 0x4004667F, arg));   // FIONREAD
            Assert.Equal(0u, p.Memory.Read32(arg));
            Send(s, "twelve bytes", Addr("127.0.0.1", port));
            AwaitReadable(s);
            Assert.Equal(0u, W("ioctlsocket", s, 0x4004667F, arg));
            Assert.Equal(12u, p.Memory.Read32(arg));   // the next datagram, exactly

            Assert.Equal(Invalid, W("ioctlsocket", s, 0x12345678, arg));
            Assert.Equal(10022u, Last());

            // Non-blocking, and back to blocking.
            NonBlocking(s);
            Assert.Equal(12u, W("recv", s, Alloc(32), 32, 0));
            Assert.Equal(Invalid, W("recv", s, Alloc(32), 32, 0));
            Assert.Equal(WouldBlock, Last());
            p.Memory.Write32(arg, 0);
            Assert.Equal(0u, W("ioctlsocket", s, 0x8004667E, arg));
            Assert.Equal(0u, W("setsockopt", s, SolSocket, SoRcvTimeo, Dword(60), 4));
            Assert.Equal(Invalid, W("recv", s, Alloc(32), 32, 0));
            Assert.Equal(TimedOut, Last());   // it waited for the timeout instead of failing at once
        }

        [Fact]
        public void ADatagramLongerThanTheBufferFillsItAndFailsWithMessageSize()
        {
            var s = BoundUdp(out var port);
            Send(s, "hello nativra", Addr("127.0.0.1", port));
            AwaitReadable(s);
            var buffer = Alloc(16);
            Assert.Equal(Invalid, W("recvfrom", s, buffer, 4, 0, 0, 0));
            Assert.Equal(MsgSize, Last());
            Assert.Equal("hell", p.Memory.ReadAnsi(buffer, 4));
            // The datagram is gone, as on Windows.
            NonBlocking(s);
            Assert.Equal(Invalid, W("recv", s, buffer, 16, 0));
            Assert.Equal(WouldBlock, Last());
        }

        [Fact]
        public void PeekLeavesTheDatagramInPlace()
        {
            var s = BoundUdp(out var port);
            Send(s, "peek me", Addr("127.0.0.1", port));
            AwaitReadable(s);
            var buffer = Alloc(16);
            Assert.Equal(7u, W("recv", s, buffer, 16, 2));   // MSG_PEEK
            Assert.Equal(7u, W("recv", s, buffer, 16, 0));
            Assert.Equal("peek me", p.Memory.ReadAnsi(buffer));
        }

        [Fact]
        public void UdpSendRulesFollowWindows()
        {
            var s = BoundUdp(out var port);
            Assert.Equal(Invalid, W("send", s, Ansi("x"), 1, 0));   // not connected, and no destination
            Assert.Equal(10039u, Last());                            // WSAEDESTADDRREQ
            Assert.Equal(Invalid, W("sendto", s, Ansi("x"), 1, 0, Addr("0.0.0.0", 9), 16));
            Assert.Equal(10049u, Last());                            // WSAEADDRNOTAVAIL
            Assert.Equal(Invalid, W("sendto", s, Ansi("x"), 1, 0, Addr("127.0.0.1", 0), 16));
            Assert.Equal(10049u, Last());
            Assert.Equal(Invalid, W("sendto", s, Ansi("x"), 1, 0, Addr("255.255.255.255", 9), 16));
            Assert.Equal(10013u, Last());                            // WSAEACCES: broadcast was not asked for
            Assert.Equal(Invalid, W("sendto", s, Alloc(70000), 70000, 0, Addr("127.0.0.1", port), 16));
            Assert.Equal(MsgSize, Last());                           // too big for a datagram

            // connect gives a datagram socket its peer; send and recv then work without addresses.
            var peer = BoundUdp(out var peerPort);
            Assert.Equal(0u, W("connect", s, Addr("127.0.0.1", peerPort), 16));
            Send(s, "to the peer");
            AwaitReadable(peer);
            var from = Alloc(16);
            var fromLength = Dword(16);
            var buffer = Alloc(32);
            Assert.Equal(11u, W("recvfrom", peer, buffer, 32, 0, from, fromLength));
            Assert.Equal(port, PortOf(from));
            var name = Alloc(16);
            Assert.Equal(0u, W("getpeername", s, name, Dword(16)));
            Assert.Equal(peerPort, PortOf(name));
        }

        [Fact]
        public void ConnectToAClosedPortIsRefusedAndShowsInTheExceptSet()
        {
            // A port nobody listens on: bind one and let it go.
            int closed;
            using (var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                closed = ((IPEndPoint)probe.LocalEndPoint).Port;
            }
            var client = Socket(1, 6);
            NonBlocking(client);
            var result = W("connect", client, Addr("127.0.0.1", closed), 16);
            Assert.Equal(Invalid, result);
            if (Last() == WouldBlock)
            {
                // The usual way: the failure arrives later, in the except set (never the write set, as on Windows).
                var write = FdSet(client);
                var except = FdSet(client);
                Assert.Equal(1u, W("select", 0, 0, write, except, Timeval(10000)));   // a Windows host takes a second or two to give up
                Assert.Empty(Members(write));
                Assert.Equal(new[] { client }, Members(except));
                var value = Dword(0);
                Assert.Equal(0u, W("getsockopt", client, SolSocket, SoError, value, Dword(4)));
                Assert.Equal(10061u, p.Memory.Read32(value));   // WSAECONNREFUSED, reported once
                Assert.Equal(0u, W("getsockopt", client, SolSocket, SoError, value, Dword(4)));
                Assert.Equal(0u, p.Memory.Read32(value));
            }
            else Assert.Equal(10061u, Last());   // a host that refuses at once says so at once

            // A blocking connect to it fails the same way.
            var blocking = Socket(1, 6);
            Assert.Equal(Invalid, W("connect", blocking, Addr("127.0.0.1", closed), 16));
            Assert.Equal(10061u, Last());
        }

        // --- WSAPoll -----------------------------------------------------------------------------------------

        [Fact]
        public void WsaPollReportsReadableWritableAndInvalidSockets()
        {
            var s = BoundUdp(out var port);
            var array = Alloc(16);   // two WSAPOLLFDs: { SOCKET fd; SHORT events; SHORT revents; }
            p.Memory.Write32(array, s);
            p.Memory.Write16(array + 4, 0x0100);   // POLLRDNORM
            p.Memory.Write32(array + 8, 0x7777);   // not a socket
            p.Memory.Write16(array + 12, 0x0100);

            // Idle: only the invalid one reports (POLLNVAL), and polling with a time to wait waits.
            var watch = Stopwatch.StartNew();
            Assert.Equal(1u, W("WSAPoll", array, 2, 0));
            Assert.Equal(0, p.Memory.Read16(array + 6));
            Assert.Equal(4, p.Memory.Read16(array + 14));   // POLLNVAL

            p.Memory.Write32(array + 8, s);
            p.Memory.Write16(array + 12, 0x0010);           // POLLWRNORM
            Assert.Equal(1u, W("WSAPoll", array, 2, 100));
            Assert.Equal(0, p.Memory.Read16(array + 6));
            Assert.Equal(0x0010, p.Memory.Read16(array + 14));

            // Nothing to read for the first: poll only that one, and it times out.
            Assert.Equal(0u, W("WSAPoll", array, 1, 120));
            Assert.True(watch.ElapsedMilliseconds >= 100);

            Send(s, "x", Addr("127.0.0.1", port));
            Assert.Equal(1u, W("WSAPoll", array, 1, 3000));
            Assert.Equal(0x0100, p.Memory.Read16(array + 6));
            Assert.Equal(Invalid, W("WSAPoll", array, 0, 0));   // nothing to poll
        }

        // --- WSAEventSelect ------------------------------------------------------------------------------------

        [Fact]
        public void EventSelectSignalsTheEventAndEnumNetworkEventsReportsAndResetsIt()
        {
            var s = BoundUdp(out var port);
            var ev = W("WSACreateEvent");
            Assert.NotEqual(0u, ev);
            Assert.Equal(0u, W("WSAEventSelect", s, ev, FdRead | FdWrite));

            // A datagram socket is writable from the start: FD_WRITE is reported at once.
            Assert.Equal(0u, K("WaitForSingleObject", ev, 0));
            var events = Alloc(44);   // WSANETWORKEVENTS: long lNetworkEvents; int iErrorCode[10];
            Assert.Equal(0u, W("WSAEnumNetworkEvents", s, ev, events));
            Assert.Equal(FdWrite, p.Memory.Read32(events));
            Assert.Equal(0x102u, K("WaitForSingleObject", ev, 0));   // WAIT_TIMEOUT: the enumeration reset it

            // Then FD_READ when a datagram arrives; WSAWaitForMultipleEvents waits for it.
            Send(s, "wake up", Addr("127.0.0.1", port));
            var handles = Dword(ev);
            Assert.Equal(0u, W("WSAWaitForMultipleEvents", 1, handles, 0, 3000, 0));   // WSA_WAIT_EVENT_0
            Assert.Equal(0u, W("WSAEnumNetworkEvents", s, ev, events));
            Assert.Equal(FdRead, p.Memory.Read32(events));
            Assert.Equal(0u, p.Memory.Read32(events + 4));   // iErrorCode[FD_READ_BIT]

            // The socket was made non-blocking, and may not be made blocking while the events are on.
            Assert.Equal(Invalid, W("ioctlsocket", s, 0x8004667E, Dword(0)));
            Assert.Equal(10022u, Last());

            // After the recv that acts on FD_READ, more data reports again.
            Assert.Equal(7u, W("recv", s, Alloc(16), 16, 0));
            Assert.Equal(0x102u, W("WSAWaitForMultipleEvents", 1, handles, 0, 50, 0));   // WSA_WAIT_TIMEOUT
            Send(s, "again", Addr("127.0.0.1", port));
            Assert.Equal(0u, W("WSAWaitForMultipleEvents", 1, handles, 0, 3000, 0));
            Assert.Equal(0u, W("WSAEnumNetworkEvents", s, ev, events));
            Assert.Equal(FdRead, p.Memory.Read32(events));

            // Taken off the event again, nothing more is reported.
            Assert.Equal(0u, W("WSAEventSelect", s, 0, 0));
            Assert.Equal(1u, W("WSACloseEvent", ev));
        }

        [Fact]
        public void EventSelectReportsAcceptConnectAndClose()
        {
            var listener = Socket(1, 6);
            Assert.Equal(0u, W("bind", listener, Addr("127.0.0.1", 0), 16));
            Assert.Equal(0u, W("listen", listener, 5));
            var port = PortOfSocket(listener);
            var acceptEvent = W("WSACreateEvent");
            Assert.Equal(0u, W("WSAEventSelect", listener, acceptEvent, FdAccept));

            var client = Socket(1, 6);
            var clientEvent = W("WSACreateEvent");
            Assert.Equal(0u, W("WSAEventSelect", client, clientEvent, FdConnect | FdRead | FdClose | FdWrite));
            Assert.Equal(Invalid, W("connect", client, Addr("127.0.0.1", port), 16));
            Assert.Equal(WouldBlock, Last());

            var events = Alloc(44);
            Assert.Equal(0u, W("WSAWaitForMultipleEvents", 1, Dword(clientEvent), 0, 3000, 0));
            Assert.Equal(0u, W("WSAEnumNetworkEvents", client, clientEvent, events));
            Assert.Equal(FdConnect | FdWrite, p.Memory.Read32(events));
            Assert.Equal(0u, p.Memory.Read32(events + 4 + 4 * 4));   // iErrorCode[FD_CONNECT_BIT]: connected

            Assert.Equal(0u, W("WSAWaitForMultipleEvents", 1, Dword(acceptEvent), 0, 3000, 0));
            Assert.Equal(0u, W("WSAEnumNetworkEvents", listener, acceptEvent, events));
            Assert.Equal(FdAccept, p.Memory.Read32(events));
            var server = W("accept", listener, 0, 0);
            Assert.NotEqual(Invalid, server);

            // The peer closing is FD_CLOSE; the data it sent first is FD_READ.
            Send(server, "bye");
            Assert.Equal(0u, W("closesocket", server));
            Assert.Equal(0u, W("WSAWaitForMultipleEvents", 1, Dword(clientEvent), 0, 3000, 0));
            Assert.Equal(0u, W("WSAEnumNetworkEvents", client, clientEvent, events));
            Assert.NotEqual(0u, p.Memory.Read32(events) & FdRead);
            Assert.Equal("bye", Receive(client));
            Assert.Equal(0u, W("WSAWaitForMultipleEvents", 1, Dword(clientEvent), 0, 3000, 0));
            Assert.Equal(0u, W("WSAEnumNetworkEvents", client, clientEvent, events));
            Assert.Equal(FdClose, p.Memory.Read32(events) & FdClose);
            Assert.Equal(0u, W("recv", client, Alloc(8), 8, 0));
        }

        // --- overlapped I/O ---------------------------------------------------------------------------------------

        // WSABUF { u_long len; char *buf; }, OVERLAPPED { Internal, InternalHigh, Offset, OffsetHigh, hEvent }.
        private uint WsaBuf(uint buffer, uint length)
        {
            var at = Alloc(8);
            p.Memory.Write32(at, length);
            p.Memory.Write32(at + 4, buffer);
            return at;
        }

        private uint Overlapped(uint eventHandle)
        {
            var at = Alloc(20);
            p.Memory.Write32(at + 16, eventHandle);
            return at;
        }

        [Fact]
        public void OverlappedReceiveStaysPendingAndCompletesThroughItsEvent()
        {
            var s = BoundUdp(out var port);
            var ev = W("WSACreateEvent");
            var buffer = Alloc(64);
            var overlapped = Overlapped(ev);
            var received = Dword(0);

            Assert.Equal(Invalid, W("WSARecv", s, WsaBuf(buffer, 64), 1, received, Dword(0), overlapped, 0));
            Assert.Equal(997u, Last());   // WSA_IO_PENDING
            Assert.Equal(0x103u, p.Memory.Read32(overlapped));   // STATUS_PENDING
            var transferred = Dword(0);
            Assert.Equal(0u, W("WSAGetOverlappedResult", s, overlapped, transferred, 0, Dword(0)));
            Assert.Equal(996u, Last());   // WSA_IO_INCOMPLETE
            Assert.Equal(0x102u, K("WaitForSingleObject", ev, 0));

            Send(s, "overlapped", Addr("127.0.0.1", port));
            Assert.Equal(0u, W("WSAWaitForMultipleEvents", 1, Dword(ev), 0, 3000, 0));
            Assert.Equal(1u, W("WSAGetOverlappedResult", s, overlapped, transferred, 0, Dword(0)));
            Assert.Equal(10u, p.Memory.Read32(transferred));
            Assert.Equal("overlapped", p.Memory.ReadAnsi(buffer));
            Assert.Equal(0u, p.Memory.Read32(overlapped));   // STATUS_SUCCESS

            // WSAGetOverlappedResult can wait for it, and so can the kernel32 call.
            Assert.Equal(1u, W("WSAResetEvent", ev));
            var second = Overlapped(ev);
            Assert.Equal(Invalid, W("WSARecv", s, WsaBuf(buffer, 64), 1, received, Dword(0), second, 0));
            Assert.Equal(997u, Last());
            Task.Delay(100).ContinueWith(_ => { using (var sender = new UdpClient()) sender.Send(new byte[] { 9, 9, 9 }, 3, new IPEndPoint(IPAddress.Loopback, port)); });
            p.DeadlockMilliseconds = 30;
            Assert.Equal(1u, K("GetOverlappedResult", s, second, transferred, 1));
            Assert.Equal(3u, p.Memory.Read32(transferred));
        }

        [Fact]
        public void OverlappedReceiveCompletesAtOnceWhenDataIsThereAndQueuesItsPacket()
        {
            var s = BoundUdp(out var port);
            var ev = W("WSACreateEvent");
            var port2 = K("CreateIoCompletionPort", Invalid, 0, 0, 1);
            Assert.Equal(port2, K("CreateIoCompletionPort", s, port2, 0x77, 0));

            Send(s, "ready", Addr("127.0.0.1", port));
            AwaitReadable(s);
            var buffer = Alloc(64);
            var overlapped = Overlapped(ev);
            var received = Dword(0);
            Assert.Equal(0u, W("WSARecv", s, WsaBuf(buffer, 64), 1, received, Dword(0), overlapped, 0));
            Assert.Equal(5u, p.Memory.Read32(received));
            Assert.Equal("ready", p.Memory.ReadAnsi(buffer));
            Assert.Equal(0u, K("WaitForSingleObject", ev, 0));   // signalled, as on Windows

            var bytes = Dword(0);
            var key = Dword(0);
            var which = Dword(0);
            Assert.Equal(1u, K("GetQueuedCompletionStatus", port2, bytes, key, which, 0));
            Assert.Equal(5u, p.Memory.Read32(bytes));
            Assert.Equal(0x77u, p.Memory.Read32(key));
            Assert.Equal(overlapped, p.Memory.Read32(which));
        }

        [Fact]
        public void PendingOverlappedReceiveCompletesOnACompletionPortAndEndsAbortedOnClose()
        {
            var s = BoundUdp(out var port);
            var completion = K("CreateIoCompletionPort", Invalid, 0, 0, 1);
            Assert.Equal(completion, K("CreateIoCompletionPort", s, completion, 0x55, 0));
            var buffer = Alloc(64);
            var first = Overlapped(0);
            Assert.Equal(Invalid, W("WSARecv", s, WsaBuf(buffer, 64), 1, 0, Dword(0), first, 0));
            Assert.Equal(997u, Last());
            var second = Overlapped(0);
            Assert.Equal(Invalid, W("WSARecv", s, WsaBuf(Alloc(64), 64), 1, 0, Dword(0), second, 0));
            Assert.Equal(997u, Last());

            Send(s, "for the first", Addr("127.0.0.1", port));
            var bytes = Dword(0);
            var key = Dword(0);
            var which = Dword(0);
            p.DeadlockMilliseconds = 30;
            Assert.Equal(1u, K("GetQueuedCompletionStatus", completion, bytes, key, which, 3000));
            Assert.Equal(13u, p.Memory.Read32(bytes));
            Assert.Equal(0x55u, p.Memory.Read32(key));
            Assert.Equal(first, p.Memory.Read32(which));   // in the order they were made
            Assert.Equal("for the first", p.Memory.ReadAnsi(buffer));

            // The other one is still waiting; closing the socket ends it, aborted, with a packet.
            Assert.Equal(0u, K("GetQueuedCompletionStatus", completion, bytes, key, which, 0));
            Assert.Equal(258u, K("GetLastError"));
            Assert.Equal(0u, W("closesocket", s));
            Assert.Equal(0u, K("GetQueuedCompletionStatus", completion, bytes, key, which, 0));   // fails: the call was aborted
            Assert.Equal(second, p.Memory.Read32(which));
            Assert.Equal(995u, K("GetLastError"));   // ERROR_OPERATION_ABORTED
        }

        [Fact]
        public void OverlappedSendOverTcpCompletesAndWsaSendWithoutOverlappedActsLikeSend()
        {
            var listener = Socket(1, 6);
            Assert.Equal(0u, W("bind", listener, Addr("127.0.0.1", 0), 16));
            Assert.Equal(0u, W("listen", listener, 5));
            var port = PortOfSocket(listener);
            var client = Socket(1, 6);
            Assert.Equal(0u, W("connect", client, Addr("127.0.0.1", port), 16));
            var server = W("accept", listener, 0, 0);

            var ev = W("WSACreateEvent");
            var overlapped = Overlapped(ev);
            var sent = Dword(0);
            Assert.Equal(0u, W("WSASend", client, WsaBuf(Ansi("overlapped send"), 15), 1, sent, 0, overlapped, 0));
            Assert.Equal(15u, p.Memory.Read32(sent));
            Assert.Equal(0u, K("WaitForSingleObject", ev, 0));
            AwaitReadable(server);
            Assert.Equal("overlapped send", Receive(server));

            // No OVERLAPPED: the socket's own mode decides, and the byte count comes back through the pointer.
            Assert.Equal(0u, W("WSASend", server, WsaBuf(Ansi("plain"), 5), 1, sent, 0, 0, 0));
            Assert.Equal(5u, p.Memory.Read32(sent));
            var buffer = Alloc(16);
            var received = Dword(0);
            var flags = Dword(0);
            Assert.Equal(0u, W("WSARecv", client, WsaBuf(buffer, 16), 1, received, flags, 0, 0));
            Assert.Equal(5u, p.Memory.Read32(received));
            Assert.Equal("plain", p.Memory.ReadAnsi(buffer));

            // A completion routine would run as an APC, which is not offered.
            Assert.Equal(Invalid, W("WSASend", server, WsaBuf(Ansi("x"), 1), 1, sent, 0, overlapped, 0x600000));
            Assert.Equal(10045u, Last());
        }

        // --- the things a game program does with it ----------------------------------------------------------------

        [Fact]
        public void AServerLoopOnUdpThePatternSrcdsUses()
        {
            // socket, FIONBIO, SO_BROADCAST, bind to INADDR_ANY, getsockname, then sendto/recvfrom with select to sleep.
            var s = Socket(2, 17);
            NonBlocking(s);
            Assert.Equal(0u, W("setsockopt", s, SolSocket, SoBroadcast, Dword(1), 4));
            Assert.Equal(0u, W("bind", s, Addr("0.0.0.0", 0), 16));
            var port = PortOfSocket(s);

            var from = Alloc(16);
            var fromLength = Dword(16);
            var buffer = Alloc(1500);
            for (var frame = 0; frame < 3; frame++)
            {
                p.Memory.Write32(fromLength, 16);
                Assert.Equal(Invalid, W("recvfrom", s, buffer, 1500, 0, from, fromLength));
                Assert.Equal(WouldBlock, Last());
                var set = FdSet(s);
                Assert.Equal(0u, W("select", (uint)s + 1, set, 0, 0, Timeval(5)));   // the frame's sleep
            }

            // A client elsewhere asks: the packet is read and answered to where it came from.
            using (var client = new UdpClient(AddressFamily.InterNetwork))
            {
                client.Client.ReceiveTimeout = 3000;
                client.Send(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x54, 0x00 }, 6, new IPEndPoint(IPAddress.Loopback, port));
                AwaitReadable(s);
                p.Memory.Write32(fromLength, 16);
                var n = W("recvfrom", s, buffer, 1500, 0, from, fromLength);
                Assert.Equal(6u, n);
                Assert.Equal(0x54, p.Memory.Read8(buffer + 4));
                Assert.Equal("127.0.0.1", p.Memory.ReadAnsi(W("inet_ntoa", p.Memory.Read32(from + 4))));
                Assert.Equal(7u, W("sendto", s, Ansi("answer!"), 7, 0, from, 16));
                var remote = new IPEndPoint(IPAddress.Any, 0);
                Assert.Equal("answer!", System.Text.Encoding.ASCII.GetString(client.Receive(ref remote)));
                Assert.Equal(port, remote.Port);
            }
        }

        [SkippableFact]
        public void Ipv6DatagramSocketsWorkToo()
        {
            Skip.IfNot(System.Net.Sockets.Socket.OSSupportsIPv6, "this host has no IPv6");
            var s = W("socket", 23, 2, 17);
            Assert.NotEqual(Invalid, s);
            var address = Alloc(28);   // sockaddr_in6: family, port, flowinfo, address, scope
            p.Memory.Write16(address, 23);
            p.Memory.WriteBytes(address + 8, IPAddress.IPv6Loopback.GetAddressBytes());
            Assert.Equal(0u, W("bind", s, address, 28));
            Assert.Equal(Invalid, W("bind", Socket(2, 17), address, 28));   // an IPv6 address on an IPv4 socket
            Assert.Equal(10047u, Last());

            var name = Alloc(28);
            var length = Dword(28);
            Assert.Equal(0u, W("getsockname", s, name, length));
            Assert.Equal(28u, p.Memory.Read32(length));
            Assert.Equal(23, p.Memory.Read16(name));
            var port = PortOf(name);
            Assert.NotEqual(0, port);

            p.Memory.Write8(address + 2, (byte)(port >> 8));
            p.Memory.Write8(address + 3, (byte)port);
            Assert.Equal(2u, W("sendto", s, Ansi("v6"), 2, 0, address, 28));
            AwaitReadable(s);
            var from = Alloc(28);
            var fromLength = Dword(28);
            var buffer = Alloc(16);
            Assert.Equal(2u, W("recvfrom", s, buffer, 16, 0, from, fromLength));
            Assert.Equal("v6", p.Memory.ReadAnsi(buffer));
            Assert.Equal(28u, p.Memory.Read32(fromLength));
            Assert.Equal(IPAddress.IPv6Loopback.GetAddressBytes(), p.Memory.ReadBytes(from + 8, 16));
            Assert.Equal(port, PortOf(from));

            // An IPv4 destination on an IPv6 socket is refused.
            Assert.Equal(Invalid, W("sendto", s, Ansi("x"), 1, 0, Addr("127.0.0.1", port), 16));
            Assert.Equal(10047u, Last());

            // getaddrinfo with a v6 literal, and the family the hints ask for.
            var result = Alloc(4);
            var hints = Alloc(32);
            p.Memory.Write32(hints + 4, 23);
            Assert.Equal(0u, W("getaddrinfo", Ansi("::1"), Ansi("80"), hints, result));
            var info = p.Memory.Read32(result);
            Assert.Equal(23u, p.Memory.Read32(info + 4));
            Assert.Equal(28u, p.Memory.Read32(info + 16));
            Assert.Equal(80, PortOf(p.Memory.Read32(info + 24)));
            W("freeaddrinfo", info);
            p.Memory.Write32(hints + 4, 2);   // AF_INET: an IPv6 literal cannot satisfy it
            Assert.Equal(11001u, W("getaddrinfo", Ansi("::1"), 0, hints, result));
            Assert.Equal(0u, W("inet_pton", 23, Ansi("fe80::1%2"), Alloc(16)));   // a zone is not part of what inet_pton takes
            Assert.Equal(1u, W("inet_pton", 23, Ansi("fe80::1"), Alloc(16)));
        }

        [Fact]
        public void WsaSendMsgSendsTheMessagesBuffers()
        {
            var receiver = BoundUdp(out var port);
            var sender = Socket(2, 17);
            var buffers = Alloc(16);   // two WSABUFs: "multi" and "part"
            var first = Ansi("multi");
            var second = Ansi("part");
            p.Memory.Write32(buffers, 5);
            p.Memory.Write32(buffers + 4, first);
            p.Memory.Write32(buffers + 8, 4);
            p.Memory.Write32(buffers + 12, second);
            var message = Alloc(28);   // WSAMSG: name, namelen, lpBuffers, dwBufferCount, Control {len, buf}, dwFlags
            p.Memory.Write32(message, Addr("127.0.0.1", port));
            p.Memory.Write32(message + 4, 16);
            p.Memory.Write32(message + 8, buffers);
            p.Memory.Write32(message + 12, 2);
            var sent = Dword(0);
            Assert.Equal(0u, W("WSASendMsg", sender, message, 0, sent, 0, 0));
            Assert.Equal(9u, p.Memory.Read32(sent));
            AwaitReadable(receiver);
            Assert.Equal("multipart", Receive(receiver));

            // And WSASendTo / WSARecvFrom with two buffers on each side.
            var gather = Alloc(8);
            p.Memory.Write32(gather, 3);
            p.Memory.Write32(gather + 4, Ansi("abc"));
            Assert.Equal(0u, W("WSASendTo", sender, gather, 1, sent, 0, Addr("127.0.0.1", port), 16, 0, 0));
            AwaitReadable(receiver);
            var scatter = Alloc(16);
            var head = Alloc(2);
            var tail = Alloc(8);
            p.Memory.Write32(scatter, 2);
            p.Memory.Write32(scatter + 4, head);
            p.Memory.Write32(scatter + 8, 8);
            p.Memory.Write32(scatter + 12, tail);
            var received = Dword(0);
            var from = Alloc(16);
            Assert.Equal(0u, W("WSARecvFrom", receiver, scatter, 2, received, Dword(0), from, Dword(16), 0, 0));
            Assert.Equal(3u, p.Memory.Read32(received));
            Assert.Equal("ab", p.Memory.ReadAnsi(head, 2).Substring(0, 2));
            Assert.Equal("c", p.Memory.ReadAnsi(tail));
        }

        [Fact]
        public void WsaStartupFillsTheDataAndNegotiatesTheVersion()
        {
            var data = Alloc(400);
            Assert.Equal(0u, W("WSAStartup", 0x0101, data));
            Assert.Equal(0x0101, p.Memory.Read16(data));
            Assert.Equal(0x0202, p.Memory.Read16(data + 2));
            Assert.Equal("WinSock 2.0", p.Memory.ReadAnsi(data + 4));
            Assert.Equal(0u, W("WSAStartup", 0x0303, data));
            Assert.Equal(0x0202, p.Memory.Read16(data));   // the highest it has
            Assert.Equal(10092u, W("WSAStartup", 0x0000, data));   // WSAVERNOTSUPPORTED
            Assert.Equal(10014u, W("WSAStartup", 0x0202, 0));
            W("WSASetLastError", 1234);
            Assert.Equal(1234u, Last());
            Assert.Equal(1234u, K("GetLastError"));
        }

        [Fact]
        public void TheSameImportsAnswerThroughWsock32ByNameAndOrdinal()
        {
            var sentinelByName = p.Imports.Bind("wsock32.dll", "socket", -1);
            var sentinelByOrdinal = p.Imports.Bind("wsock32.dll", null, 23);
            Assert.True(p.Imports.TryResolve(sentinelByName, out var byName) && byName.Handler != null);
            Assert.True(p.Imports.TryResolve(sentinelByOrdinal, out var byOrdinal) && byOrdinal.Handler != null);
            Assert.True(p.Call(sentinelByOrdinal, out var s, 1_000_000, 2, 2, 17).Ok);
            Assert.NotEqual(Invalid, s);
            Assert.True(p.Call(p.Imports.Bind("wsock32.dll", null, 3), out var closed, 1_000_000, s).Ok);   // closesocket
            Assert.Equal(0u, closed);
        }

        [Fact]
        public void Wsock32NumbersItsThreeOddOnesOutOfOrderFromWs2_32()
        {
            // wsock32 keeps the Winsock 1.1 order: inet_addr 10, inet_ntoa 11, ioctlsocket 12.
            // ws2_32 has ioctlsocket 10, inet_addr 11, inet_ntoa 12. (srcds calls wsock32 #12 as ioctlsocket.)
            uint ByOrdinal(string module, int ordinal, params uint[] args)
            {
                Assert.True(p.Call(p.Imports.Bind(module, null, ordinal), out var eax, 1_000_000, args).Ok);
                return eax;
            }
            var s = BoundUdp(out _);
            Assert.Equal(0u, ByOrdinal("wsock32.dll", 12, s, 0x8004667E, Dword(1)));     // ioctlsocket(FIONBIO)
            Assert.Equal(Invalid, W("recv", s, Alloc(4), 4, 0));
            Assert.Equal(WouldBlock, Last());   // it did not wait: the socket is non-blocking now
            Assert.Equal(0u, ByOrdinal("ws2_32.dll", 10, s, 0x8004667E, Dword(0)));      // ioctlsocket(FIONBIO) back to blocking
            Assert.Equal(0u, W("setsockopt", s, SolSocket, SoRcvTimeo, Dword(40), 4));
            Assert.Equal(Invalid, W("recv", s, Alloc(4), 4, 0));
            Assert.Equal(TimedOut, Last());     // it waited

            Assert.Equal(0x0100007Fu, ByOrdinal("wsock32.dll", 10, Ansi("127.0.0.1")));   // inet_addr
            Assert.Equal(0x0100007Fu, ByOrdinal("ws2_32.dll", 11, Ansi("127.0.0.1")));
            Assert.Equal("10.0.0.1", p.Memory.ReadAnsi(ByOrdinal("wsock32.dll", 11, 0x0100000A)));   // inet_ntoa
            Assert.Equal("10.0.0.1", p.Memory.ReadAnsi(ByOrdinal("ws2_32.dll", 12, 0x0100000A)));
        }
    }
}
