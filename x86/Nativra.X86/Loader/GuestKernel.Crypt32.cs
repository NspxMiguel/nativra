using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Nativra.X86.Cpu;

namespace Nativra.X86.Loader
{
    // crypt32's certificate stores and chain building. A store is an
    // in-memory list of certificate contexts (the system stores are the same
    // thing, empty: the guest has no machine trust store of its own), a
    // context is the CERT_CONTEXT a program reads CERT_INFO out of, with the
    // reference counts that decide when its memory goes, and a chain is built
    // by the host's X.509 engine against the host's trusted roots, then laid
    // out as the CERT_CHAIN_CONTEXT structures the guest walks.
    public sealed partial class GuestKernel
    {
        private const uint CryptEExists = 0x80092005, CryptEAsn1Eod = 0x80093102, CryptEAsn1BadTag = 0x8009310B,
            CryptEPendingClose = 0x80092012, EInvalidArg = 0x80070057;
        private const uint X509AsnEncoding = 1;

        private sealed class CertContext
        {
            public uint Address;          // the CERT_CONTEXT, which is also the start of its one heap block
            public byte[] Der;
            public X509Certificate2 Cert;
            public uint Store;            // the store handle it belongs to, 0 for none
            public int References = 1;
        }

        private sealed class CertStore
        {
            public readonly List<CertContext> Certs = new List<CertContext>();
        }

        /// <summary>What a CERT_CHAIN_CONTEXT owns: its heap blocks and the certificate contexts of its elements.</summary>
        private sealed class CertChain
        {
            public readonly List<uint> Blocks = new List<uint>();
            public readonly List<CertContext> Contexts = new List<CertContext>();
        }

        private readonly Dictionary<uint, CertStore> certStores = new Dictionary<uint, CertStore>();
        private readonly Dictionary<uint, CertContext> certContexts = new Dictionary<uint, CertContext>();
        private readonly Dictionary<uint, CertChain> certChains = new Dictionary<uint, CertChain>();
        private readonly object certLock = new object();

        // --- contexts ---------------------------------------------------------------------------

        /// <summary>
        /// Lays a certificate out as a CERT_CONTEXT in one heap block: the context, its
        /// CERT_INFO, and everything the CERT_INFO points at (the DER copy, issuer and
        /// subject names, serial number, public key, extensions).
        /// </summary>
        private CertContext NewCertContext(byte[] der, X509Certificate2 cert, uint store)
        {
            var blobs = new List<byte[]>();   // appended after the fixed part, each addressed by index
            int Add(byte[] bytes) { blobs.Add(bytes ?? new byte[0]); return blobs.Count - 1; }
            var serial = HexToBytes(cert.SerialNumber);
            Array.Reverse(serial);            // CRYPT_INTEGER_BLOB is little-endian, the text is not
            var derAt = Add(der);
            var issuerAt = Add(cert.IssuerName.RawData);
            var subjectAt = Add(cert.SubjectName.RawData);
            var serialAt = Add(serial);
            var signatureOid = Add(AsciiZ(cert.SignatureAlgorithm?.Value));
            var keyOid = Add(AsciiZ(cert.PublicKey.Oid?.Value));
            var keyParameters = Add(cert.PublicKey.EncodedParameters?.RawData);
            var keyBytes = Add(cert.PublicKey.EncodedKeyValue?.RawData);
            var extensions = cert.Extensions;
            var extensionOid = new int[extensions.Count];
            var extensionValue = new int[extensions.Count];
            for (var n = 0; n < extensions.Count; n++)
            {
                extensionOid[n] = Add(AsciiZ(extensions[n].Oid?.Value));
                extensionValue[n] = Add(extensions[n].RawData);
            }

            const int contextSize = 20, infoSize = 112;
            var arrayAt = contextSize + infoSize;
            var fixedSize = arrayAt + 16 * extensions.Count;
            var offsets = new uint[blobs.Count];
            var total = (uint)fixedSize;
            for (var n = 0; n < blobs.Count; n++) { offsets[n] = total; total += ((uint)blobs[n].Length + 3) & ~3u; }

            var address = heap.Alloc(Math.Max(total, 1u), zero: true);
            for (var n = 0; n < blobs.Count; n++) memory.WriteBytes(address + offsets[n], blobs[n]);
            uint At(int index) => address + offsets[index];

            // CERT_CONTEXT: encoding type, encoded bytes and size, CERT_INFO, store.
            memory.Write32(address, X509AsnEncoding);
            memory.Write32(address + 4, At(derAt));
            memory.Write32(address + 8, (uint)der.Length);
            memory.Write32(address + 12, address + contextSize);
            memory.Write32(address + 16, store);

            // CERT_INFO (32-bit layout).
            var info = address + contextSize;
            memory.Write32(info, (uint)Math.Max(cert.Version - 1, 0));                    // dwVersion: 0 = v1, 2 = v3
            WriteBlob(info + 4, blobs[serialAt].Length, At(serialAt));                    // SerialNumber
            memory.Write32(info + 12, At(signatureOid));                                  // SignatureAlgorithm.pszObjId; its parameters are not exposed by the host
            WriteBlob(info + 24, blobs[issuerAt].Length, At(issuerAt));                   // Issuer
            memory.Write64(info + 32, (ulong)cert.NotBefore.ToFileTimeUtc());             // NotBefore
            memory.Write64(info + 40, (ulong)cert.NotAfter.ToFileTimeUtc());             // NotAfter
            WriteBlob(info + 48, blobs[subjectAt].Length, At(subjectAt));                 // Subject
            memory.Write32(info + 56, At(keyOid));                                        // SubjectPublicKeyInfo.Algorithm.pszObjId
            WriteBlob(info + 60, blobs[keyParameters].Length, At(keyParameters));         //   .Parameters
            WriteBlob(info + 68, blobs[keyBytes].Length, At(keyBytes));                   //   .PublicKey (a CRYPT_BIT_BLOB, no unused bits)
            memory.Write32(info + 104, (uint)extensions.Count);                           // cExtension
            memory.Write32(info + 108, extensions.Count == 0 ? 0 : address + (uint)arrayAt);   // rgExtension
            for (var n = 0; n < extensions.Count; n++)
            {
                var entry = address + (uint)arrayAt + 16u * (uint)n;
                memory.Write32(entry, At(extensionOid[n]));
                memory.Write32(entry + 4, extensions[n].Critical ? 1u : 0u);
                WriteBlob(entry + 8, blobs[extensionValue[n]].Length, At(extensionValue[n]));
            }

            var context = new CertContext { Address = address, Der = der, Cert = cert, Store = store };
            lock (certLock) certContexts[address] = context;
            return context;
        }

        private void WriteBlob(uint at, int size, uint data)
        {
            memory.Write32(at, (uint)size);
            memory.Write32(at + 4, size == 0 ? 0 : data);
        }

        private static byte[] AsciiZ(string text)
        {
            var bytes = new byte[(text?.Length ?? 0) + 1];
            for (var n = 0; n < bytes.Length - 1; n++) bytes[n] = (byte)text[n];
            return bytes;
        }

        private static byte[] HexToBytes(string hex)
        {
            var bytes = new byte[hex.Length / 2];
            for (var n = 0; n < bytes.Length; n++) bytes[n] = Convert.ToByte(hex.Substring(2 * n, 2), 16);
            return bytes;
        }

        private void ReleaseContext(CertContext context)
        {
            lock (certLock)
            {
                if (--context.References > 0) return;
                certContexts.Remove(context.Address);
            }
            context.Cert.Dispose();
            heap.Free(context.Address);
        }

        private uint FreeCertContext(uint address)
        {
            if (address == 0) return 1;   // freeing NULL is allowed
            CertContext context;
            lock (certLock) certContexts.TryGetValue(address, out context);
            if (context == null) { process.LastError = EInvalidArg; return 0; }
            ReleaseContext(context);
            return 1;
        }

        /// <summary>CertCreateCertificateContext: a context that belongs to no store, or 0 with the error set.</summary>
        private uint CreateCertContext(uint encoding, uint bytes, uint length)
        {
            if ((encoding & X509AsnEncoding) == 0 || bytes == 0) { process.LastError = EInvalidArg; return 0; }
            var der = memory.ReadBytes(bytes, (int)length);
            X509Certificate2 cert;
            try { cert = new X509Certificate2(der); }
            catch (CryptographicException) { process.LastError = der.Length < 2 || der[0] != 0x30 ? CryptEAsn1BadTag : CryptEAsn1Eod; return 0; }
            return NewCertContext(der, cert, 0).Address;
        }

        // --- stores -----------------------------------------------------------------------------

        private uint OpenCertStore(uint provider, uint flags)
        {
            // CERT_STORE_PROV_MEMORY (2), and the system stores by name (9, 10): all of them start empty.
            if (provider != 2 && provider != 9 && provider != 10) { process.LastError = ErrorFileNotFound; return 0; }
            var handle = NewHandle();
            lock (certLock) certStores[handle] = new CertStore();
            return handle;
        }

        private uint AddCertToStore(uint storeHandle, uint contextAddress, uint disposition, uint result)
        {
            CertStore store;
            CertContext source;
            lock (certLock)
            {
                certStores.TryGetValue(storeHandle, out store);
                certContexts.TryGetValue(contextAddress, out source);
            }
            if (store == null || source == null) { process.LastError = EInvalidArg; return 0; }
            if (disposition < 1 || disposition > 7) { process.LastError = EInvalidArg; return 0; }

            CertContext existing = null;
            lock (certLock)
                foreach (var c in store.Certs)
                    if (c.Cert.Thumbprint == source.Cert.Thumbprint) { existing = c; break; }

            if (existing != null)
            {
                if (disposition == 1) { process.LastError = CryptEExists; return 0; }                        // CERT_STORE_ADD_NEW
                var keep = disposition == 2                                                                   // USE_EXISTING
                    || (disposition >= 6 && existing.Cert.NotBefore >= source.Cert.NotBefore);               // ADD_NEWER[_INHERIT_PROPERTIES]: only a newer one wins
                if (keep)
                {
                    if (result != 0) { memory.Write32(result, existing.Address); existing.References++; }
                    return 1;
                }
                if (disposition != 4)   // ADD_ALWAYS keeps both; the others replace
                {
                    lock (certLock) store.Certs.Remove(existing);
                    ReleaseContext(existing);
                }
            }

            // The store keeps its own copy, so the caller can free the one it passed.
            var copy = NewCertContext(source.Der, new X509Certificate2(source.Der), storeHandle);
            lock (certLock) store.Certs.Add(copy);
            if (result != 0) { copy.References++; memory.Write32(result, copy.Address); }
            return 1;
        }

        private uint CloseCertStore(uint handle, uint flags)
        {
            CertStore store;
            lock (certLock)
            {
                if (!certStores.TryGetValue(handle, out store)) { process.LastError = EInvalidArg; return 0; }
                certStores.Remove(handle);
            }
            var pending = false;
            foreach (var c in store.Certs.ToArray())
            {
                if (c.References > 1) pending = true;   // a context the program still holds outlives the store
                ReleaseContext(c);
            }
            if ((flags & 2) != 0 && pending) { process.LastError = CryptEPendingClose; return 0; }   // CERT_CLOSE_STORE_CHECK_FLAG
            return 1;
        }

        // --- chains -----------------------------------------------------------------------------

        private uint BuildCertChain(uint contextAddress, uint time, uint extraStore, uint para, uint result)
        {
            CertContext leaf;
            lock (certLock) certContexts.TryGetValue(contextAddress, out leaf);
            if (leaf == null) { process.LastError = EInvalidArg; return 0; }

            var elements = new List<X509ChainElement>();
            var chainStatus = X509ChainStatusFlags.NoError;
            using (var chain = new X509Chain())
            {
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                if (time != 0) chain.ChainPolicy.VerificationTime = DateTime.FromFileTimeUtc((long)memory.Read64(time)).ToLocalTime();
                if (extraStore != 0)
                {
                    CertStore extra;
                    lock (certLock) certStores.TryGetValue(extraStore, out extra);
                    if (extra != null)
                        lock (certLock)
                            foreach (var c in extra.Certs) chain.ChainPolicy.ExtraStore.Add(new X509Certificate2(c.Der));
                }
                // CERT_CHAIN_PARA.RequestedUsage: the enhanced key usages the leaf has to carry.
                if (para != 0 && memory.Read32(para) >= 16 && memory.Read32(para + 4) == 0)   // USAGE_MATCH_TYPE_AND
                {
                    var count = memory.Read32(para + 8);
                    var list = memory.Read32(para + 12);
                    for (var n = 0u; n < count && n < 64 && list != 0; n++)
                        chain.ChainPolicy.ApplicationPolicy.Add(new Oid(memory.ReadAnsi(memory.Read32(list + 4 * n))));
                }
                chain.Build(new X509Certificate2(leaf.Der));
                foreach (var status in chain.ChainStatus) chainStatus |= status.Status;
                foreach (X509ChainElement element in chain.ChainElements) elements.Add(element);

                // Everything is copied into guest memory before the host chain is disposed.
                var owned = new CertChain();
                var elementPointers = new uint[elements.Count];
                uint errors = 0, info = 0;
                for (var n = 0; n < elements.Count; n++)
                {
                    var elementCert = elements[n].Certificate;
                    var context = NewCertContext(elementCert.RawData, new X509Certificate2(elementCert.RawData), 0);
                    owned.Contexts.Add(context);
                    uint elementErrors = 0, elementInfo = 0;
                    foreach (var status in elements[n].ChainElementStatus) elementErrors |= (uint)status.Status;
                    if (n == elements.Count - 1 && elementCert.Subject == elementCert.Issuer) elementInfo |= 0x8;   // CERT_TRUST_IS_SELF_SIGNED
                    errors |= elementErrors;
                    info |= elementInfo;
                    var element = heap.Alloc(32, zero: true);
                    memory.Write32(element, 32);                      // CERT_CHAIN_ELEMENT: cbSize, pCertContext, TrustStatus
                    memory.Write32(element + 4, context.Address);
                    memory.Write32(element + 8, elementErrors);
                    memory.Write32(element + 12, elementInfo);
                    elementPointers[n] = element;
                    owned.Blocks.Add(element);
                }
                var array = heap.Alloc(Math.Max(4u * (uint)elements.Count, 4u), zero: true);
                for (var n = 0; n < elementPointers.Length; n++) memory.Write32(array + 4u * (uint)n, elementPointers[n]);
                var simple = heap.Alloc(32, zero: true);              // CERT_SIMPLE_CHAIN
                memory.Write32(simple, 32);
                memory.Write32(simple + 4, errors);
                memory.Write32(simple + 8, info);
                memory.Write32(simple + 12, (uint)elements.Count);
                memory.Write32(simple + 16, array);
                var simpleArray = heap.Alloc(4, zero: true);
                memory.Write32(simpleArray, simple);
                var chainContext = heap.Alloc(56, zero: true);        // CERT_CHAIN_CONTEXT
                memory.Write32(chainContext, 56);
                memory.Write32(chainContext + 4, errors | (uint)chainStatus);
                memory.Write32(chainContext + 8, info);
                memory.Write32(chainContext + 12, 1);
                memory.Write32(chainContext + 16, simpleArray);
                memory.WriteBytes(chainContext + 40, Guid.NewGuid().ToByteArray());   // ChainId
                owned.Blocks.Add(array); owned.Blocks.Add(simple); owned.Blocks.Add(simpleArray);
                lock (certLock) certChains[chainContext] = owned;
                memory.Write32(result, chainContext);
                return 1;
            }
        }

        private uint FreeCertChain(uint address)
        {
            CertChain owned;
            lock (certLock)
            {
                if (!certChains.TryGetValue(address, out owned)) return 0;
                certChains.Remove(address);
            }
            foreach (var context in owned.Contexts) ReleaseContext(context);
            foreach (var block in owned.Blocks) heap.Free(block);
            heap.Free(address);
            return 0;
        }

        private void InstallCrypt32(GuestImports i)
        {
            const string c32 = "crypt32.dll";
            i.Register(c32, "CertOpenStore", CallConv.Stdcall, 5, c => OpenCertStore(c.Arg(0), c.Arg(3)));
            i.Register(c32, "CertCloseStore", CallConv.Stdcall, 2, c => CloseCertStore(c.Arg(0), c.Arg(1)));
            i.Register(c32, "CertCreateCertificateContext", CallConv.Stdcall, 3, c => CreateCertContext(c.Arg(0), c.Arg(1), c.Arg(2)));
            i.Register(c32, "CertAddCertificateContextToStore", CallConv.Stdcall, 4, c => AddCertToStore(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3)));
            i.Register(c32, "CertFreeCertificateContext", CallConv.Stdcall, 1, c => FreeCertContext(c.Arg(0)));
            i.Register(c32, "CertDuplicateCertificateContext", CallConv.Stdcall, 1, c =>
            {
                CertContext context;
                lock (certLock) { if (certContexts.TryGetValue(c.Arg(0), out context)) context.References++; }
                return context != null ? context.Address : 0;
            });
            i.Register(c32, "CertEnumCertificatesInStore", CallConv.Stdcall, 2, c =>
            {
                CertStore store;
                lock (certLock) certStores.TryGetValue(c.Arg(0), out store);
                if (store == null) { process.LastError = EInvalidArg; return 0; }
                // The previous context is consumed (released) and the next one comes back with a reference of its own.
                CertContext next = null;
                lock (certLock)
                {
                    var index = 0;
                    if (c.Arg(1) != 0)
                    {
                        index = store.Certs.FindIndex(x => x.Address == c.Arg(1)) + 1;
                        if (index == 0) { process.LastError = EInvalidArg; return 0; }
                    }
                    if (index < store.Certs.Count) { next = store.Certs[index]; next.References++; }
                }
                if (c.Arg(1) != 0) FreeCertContext(c.Arg(1));
                if (next == null) { process.LastError = 0x80092004; return 0; }   // CRYPT_E_NOT_FOUND: the end of the store
                return next.Address;
            });
            i.Register(c32, "CertGetCertificateChain", CallConv.Stdcall, 8, c => BuildCertChain(c.Arg(1), c.Arg(2), c.Arg(3), c.Arg(4), c.Arg(7)));
            i.Register(c32, "CertFreeCertificateChain", CallConv.Stdcall, 1, c => FreeCertChain(c.Arg(0)));
        }
    }
}
