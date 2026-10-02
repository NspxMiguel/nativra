using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Nativra.X86.Cpu;
using Nativra.X86.Loader;
using Xunit;

namespace Nativra.X86.Tests
{
    /// <summary>crypt32's certificate stores, contexts and chains.</summary>
    public sealed class GuestCrypt32Tests : IDisposable
    {
        private readonly string work = Path.Combine(Path.GetTempPath(), "nativra-crypt-" + Guid.NewGuid().ToString("N"));
        private readonly GuestProcess p;
        private readonly GuestKernel k;

        public GuestCrypt32Tests()
        {
            Directory.CreateDirectory(work);
            p = new GuestProcess(new GuestMemory(native: true), useJit: false);
            k = new GuestKernel(p) { ExePath = "C:\\game\\game.exe", Files = new HostFolderFiles("C:\\game", work) };
            k.Install();
        }

        public void Dispose()
        {
            p.Dispose();
            try { Directory.Delete(work, true); } catch (IOException) { }
        }

        private uint C(string f, params uint[] a)
        {
            var result = p.Call(p.Imports.Bind("crypt32.dll", f, -1), out var eax, 50_000_000, a);
            Assert.True(result.Ok, $"{f} stopped as {result}");
            return eax;
        }

        private uint LastError() => p.Call(p.Imports.Bind("kernel32.dll", "GetLastError", -1), out var eax, 1_000_000).Ok ? eax : 0;

        private uint Guest(byte[] bytes)
        {
            var ptr = k.Heap.Alloc((uint)bytes.Length, zero: true);
            p.Memory.WriteBytes(ptr, bytes);
            return ptr;
        }

        // A throwaway root and a leaf it signed, as DER.
        private static void MakeChain(out byte[] root, out byte[] leaf)
        {
            using (var rootKey = RSA.Create(2048))
            using (var leafKey = RSA.Create(2048))
            {
                var rootRequest = new CertificateRequest("CN=Nativra Test Root", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
                using (var rootCert = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30)))
                {
                    root = rootCert.RawData;
                    var leafRequest = new CertificateRequest("CN=localhost", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                    leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
                    using (var signed = leafRequest.Create(rootCert, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10), new byte[] { 0x01, 0x02, 0x03, 0x7F }))
                        leaf = signed.RawData;
                }
            }
        }

        [Fact]
        public void ACertificateContextCarriesItsCertInfo()
        {
            MakeChain(out _, out var leaf);
            var context = C("CertCreateCertificateContext", 1, Guest(leaf), (uint)leaf.Length);
            Assert.NotEqual(0u, context);
            Assert.Equal(1u, p.Memory.Read32(context));                                   // X509_ASN_ENCODING
            Assert.Equal((uint)leaf.Length, p.Memory.Read32(context + 8));
            Assert.Equal(leaf, p.Memory.ReadBytes(p.Memory.Read32(context + 4), leaf.Length));
            Assert.Equal(0u, p.Memory.Read32(context + 16));                              // in no store

            var info = p.Memory.Read32(context + 12);
            using (var cert = new X509Certificate2(leaf))
            {
                Assert.Equal(2u, p.Memory.Read32(info));                                  // CERT_V3
                Assert.Equal(4u, p.Memory.Read32(info + 4));                              // serial length...
                Assert.Equal(new byte[] { 0x7F, 0x03, 0x02, 0x01 }, p.Memory.ReadBytes(p.Memory.Read32(info + 8), 4));   // ...little-endian
                var subject = p.Memory.ReadBytes(p.Memory.Read32(info + 52), (int)p.Memory.Read32(info + 48));
                Assert.Equal(cert.SubjectName.RawData, subject);
                var issuer = p.Memory.ReadBytes(p.Memory.Read32(info + 28), (int)p.Memory.Read32(info + 24));
                Assert.Equal(cert.IssuerName.RawData, issuer);
                Assert.Equal(cert.NotAfter.ToFileTimeUtc(), (long)p.Memory.Read64(info + 40));
                Assert.Equal("1.2.840.113549.1.1.1", p.Memory.ReadAnsi(p.Memory.Read32(info + 56)));   // rsaEncryption
                Assert.Equal(cert.PublicKey.EncodedKeyValue.RawData, p.Memory.ReadBytes(p.Memory.Read32(info + 72), (int)p.Memory.Read32(info + 68)));
                Assert.Equal((uint)cert.Extensions.Count, p.Memory.Read32(info + 104));
            }
            Assert.Equal(1u, C("CertFreeCertificateContext", context));
            Assert.Equal(0u, C("CertFreeCertificateContext", context));                  // already gone
            Assert.Equal(1u, C("CertFreeCertificateContext", 0));
        }

        [Fact]
        public void GarbageIsNotACertificate()
        {
            Assert.Equal(0u, C("CertCreateCertificateContext", 1, Guest(new byte[] { 1, 2, 3, 4 }), 4));
            Assert.Equal(0x8009310Bu, LastError());                                       // CRYPT_E_ASN1_BADTAG
            Assert.Equal(0u, C("CertCreateCertificateContext", 1, Guest(new byte[] { 0x30, 0x82, 0x01 }), 3));
            Assert.Equal(0x80093102u, LastError());                                       // CRYPT_E_ASN1_EOD
            Assert.Equal(0u, C("CertCreateCertificateContext", 0, Guest(new byte[] { 0x30, 0 }), 2));
            Assert.Equal(0x80070057u, LastError());                                       // no X.509 encoding named
        }

        [Fact]
        public void AMemoryStoreKeepsItsOwnCopiesAndHonoursTheAddDispositions()
        {
            MakeChain(out var root, out var leaf);
            var store = C("CertOpenStore", 2, 1, 0, 0, 0);                                // CERT_STORE_PROV_MEMORY
            Assert.NotEqual(0u, store);
            var context = C("CertCreateCertificateContext", 1, Guest(leaf), (uint)leaf.Length);
            var slot = k.Heap.Alloc(4, zero: true);

            Assert.Equal(1u, C("CertAddCertificateContextToStore", store, context, 1, slot));   // ADD_NEW
            var inStore = p.Memory.Read32(slot);
            Assert.NotEqual(context, inStore);                                            // a copy, belonging to the store
            Assert.Equal(store, p.Memory.Read32(inStore + 16));
            Assert.Equal(0u, C("CertAddCertificateContextToStore", store, context, 1, 0));
            Assert.Equal(0x80092005u, LastError());                                       // CRYPT_E_EXISTS
            Assert.Equal(1u, C("CertAddCertificateContextToStore", store, context, 2, slot));   // USE_EXISTING
            Assert.Equal(inStore, p.Memory.Read32(slot));

            // Enumeration sees the one certificate, then the end of the store.
            var first = C("CertEnumCertificatesInStore", store, 0);
            Assert.Equal(inStore, first);
            Assert.Equal(0u, C("CertEnumCertificatesInStore", store, first));
            Assert.Equal(0x80092004u, LastError());                                       // CRYPT_E_NOT_FOUND

            // The context handed out survives the store; closing with outstanding contexts is reported.
            Assert.Equal(1u, C("CertFreeCertificateContext", context));
            var unfreed = C("CertEnumCertificatesInStore", store, 0);
            Assert.Equal(0u, C("CertCloseStore", store, 2));                              // CERT_CLOSE_STORE_CHECK_FLAG
            Assert.Equal(0x80092012u, LastError());                                       // CRYPT_E_PENDING_CLOSE
            Assert.Equal(leaf, p.Memory.ReadBytes(p.Memory.Read32(unfreed + 4), leaf.Length));   // still readable
            Assert.Equal(1u, C("CertFreeCertificateContext", unfreed));
            Assert.Equal(0u, C("CertCloseStore", store, 0));                              // a closed store is no store
            Assert.Equal(0x80070057u, LastError());
            Assert.Equal(0u, C("CertOpenStore", 99, 1, 0, 0, 0));                         // no such provider
            Assert.Equal(2u, LastError());
        }

        [Fact]
        public void AChainFromAnUntrustedRootEndsInAnUntrustedRootError()
        {
            MakeChain(out var root, out var leaf);
            var extra = C("CertOpenStore", 2, 1, 0, 0, 0);
            var rootContext = C("CertCreateCertificateContext", 1, Guest(root), (uint)root.Length);
            Assert.Equal(1u, C("CertAddCertificateContextToStore", extra, rootContext, 4, 0));
            var leafContext = C("CertCreateCertificateContext", 1, Guest(leaf), (uint)leaf.Length);

            var slot = k.Heap.Alloc(4, zero: true);
            Assert.Equal(1u, C("CertGetCertificateChain", 0, leafContext, 0, extra, 0, 0, 0, slot));
            var chain = p.Memory.Read32(slot);
            Assert.Equal(56u, p.Memory.Read32(chain));                                    // cbSize
            Assert.NotEqual(0u, p.Memory.Read32(chain + 4) & 0x20);                       // CERT_TRUST_IS_UNTRUSTED_ROOT
            Assert.Equal(1u, p.Memory.Read32(chain + 12));                                // one simple chain
            var simple = p.Memory.Read32(p.Memory.Read32(chain + 16));
            Assert.Equal(2u, p.Memory.Read32(simple + 12));                               // leaf, root
            var elements = p.Memory.Read32(simple + 16);
            var leafElement = p.Memory.Read32(elements);
            var rootElement = p.Memory.Read32(elements + 4);
            Assert.Equal(leaf, p.Memory.ReadBytes(p.Memory.Read32(p.Memory.Read32(leafElement + 4) + 4), leaf.Length));
            Assert.Equal(root, p.Memory.ReadBytes(p.Memory.Read32(p.Memory.Read32(rootElement + 4) + 4), root.Length));
            Assert.NotEqual(0u, p.Memory.Read32(rootElement + 12) & 0x8);                 // CERT_TRUST_IS_SELF_SIGNED
            Assert.NotEqual(0u, p.Memory.Read32(rootElement + 8) & 0x20);                 // the untrusted root is the root's error

            C("CertFreeCertificateChain", chain);
            Assert.Equal(1u, C("CertFreeCertificateContext", leafContext));
            Assert.Equal(1u, C("CertFreeCertificateContext", rootContext));
            Assert.Equal(1u, C("CertCloseStore", extra, 0));
        }

        [Fact]
        public void AChainCheckedAfterTheCertificateExpiredSaysSo()
        {
            MakeChain(out _, out var leaf);
            var leafContext = C("CertCreateCertificateContext", 1, Guest(leaf), (uint)leaf.Length);
            var when = k.Heap.Alloc(8, zero: true);
            p.Memory.Write64(when, (ulong)DateTime.UtcNow.AddDays(400).ToFileTimeUtc());
            var slot = k.Heap.Alloc(4, zero: true);
            Assert.Equal(1u, C("CertGetCertificateChain", 0, leafContext, when, 0, 0, 0, 0, slot));
            var chain = p.Memory.Read32(slot);
            Assert.NotEqual(0u, p.Memory.Read32(chain + 4) & 0x1);                        // CERT_TRUST_IS_NOT_TIME_VALID
            C("CertFreeCertificateChain", chain);
        }
    }
}
