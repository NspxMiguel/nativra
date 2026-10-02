using System;
using System.IO;
using Nativra.X86.Cpu;
using Nativra.X86.Loader;
using Xunit;

namespace Nativra.X86.Tests
{
    /// <summary>advapi32's security family: SIDs, the process token, account lookup, security descriptors and ACLs.</summary>
    public sealed class GuestSecurityTests : IDisposable
    {
        private const string UserText = "S-1-5-21-1004336348-1177238915-682003330-1001";
        private readonly string work = Path.Combine(Path.GetTempPath(), "nativra-sec-" + Guid.NewGuid().ToString("N"));
        private readonly GuestProcess p;
        private readonly GuestKernel k;

        public GuestSecurityTests()
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

        private uint A(string f, params uint[] a)
        {
            var result = p.Call(p.Imports.Bind("advapi32.dll", f, -1), out var eax, 50_000_000, a);
            Assert.True(result.Ok, $"{f} stopped as {result}");
            return eax;
        }

        private uint LastError() => p.Call(p.Imports.Bind("kernel32.dll", "GetLastError", -1), out var eax, 1_000_000) .Ok ? eax : 0;

        private uint Buffer(uint size = 512) => k.Heap.Alloc(size, zero: true);

        private uint Token()
        {
            var slot = Buffer(4);
            Assert.Equal(1u, A("OpenProcessToken", 0xFFFFFFFF, 8, slot));
            return p.Memory.Read32(slot);
        }

        private uint TokenInfo(uint token, uint cls, out uint size)
        {
            var needed = Buffer(4);
            Assert.Equal(0u, A("GetTokenInformation", token, cls, 0, 0, needed));
            Assert.Equal(122u, LastError());   // ERROR_INSUFFICIENT_BUFFER, with the size reported
            size = p.Memory.Read32(needed);
            var data = Buffer(size);
            Assert.Equal(1u, A("GetTokenInformation", token, cls, data, size, needed));
            Assert.Equal(size, p.Memory.Read32(needed));
            return data;
        }

        private uint WideStr(string text)
        {
            var ptr = Buffer((uint)text.Length * 2 + 2);
            p.Memory.WriteUnicode(ptr, text);
            return ptr;
        }

        private uint SidFromText(string text)
        {
            var slot = Buffer(4);
            Assert.Equal(1u, A("ConvertStringSidToSidW", WideStr(text), slot));
            return p.Memory.Read32(slot);
        }

        private string SidText(uint sid)
        {
            var slot = Buffer(4);
            Assert.Equal(1u, A("ConvertSidToStringSidW", sid, slot));
            return p.Memory.ReadUnicode(p.Memory.Read32(slot));
        }

        [Fact]
        public void TokenUserIsOneConsistentSidInBinaryLayout()
        {
            var token = Token();
            var data = TokenInfo(token, 1, out var size);
            var sid = p.Memory.Read32(data);
            Assert.Equal(data + 8, sid);                          // the SID follows the SID_AND_ATTRIBUTES
            Assert.Equal(0u, p.Memory.Read32(data + 4));          // no attributes on the user entry
            Assert.Equal(1, p.Memory.Read8(sid));                 // revision
            Assert.Equal(5, p.Memory.Read8(sid + 1));             // five sub-authorities
            Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 5 }, p.Memory.ReadBytes(sid + 2, 6));   // NT authority, big-endian
            Assert.Equal(1001u, p.Memory.Read32(sid + 8 + 16));   // RID
            Assert.Equal(28u, A("GetLengthSid", sid));
            Assert.Equal(8u + 28u, size);
            Assert.Equal(1u, A("IsValidSid", sid));
            Assert.Equal(UserText, SidText(sid));
            // A second token gives an equal SID (EqualSid), at a different address.
            var other = p.Memory.Read32(TokenInfo(Token(), 1, out _));
            Assert.Equal(1u, A("EqualSid", sid, other));
        }

        [Fact]
        public void ConvertedStringSidsRoundTrip()
        {
            Assert.Equal("S-1-5-32-544", SidText(SidFromText("S-1-5-32-544")));
            Assert.Equal("S-1-1-0", SidText(SidFromText("S-1-1-0")));
            Assert.Equal(UserText, SidText(SidFromText(UserText)));
            Assert.Equal(0u, A("ConvertStringSidToSidW", WideStr("not-a-sid"), Buffer(4)));
            Assert.Equal(1337u, LastError());   // ERROR_INVALID_SID
        }

        [Fact]
        public void SidBuildingFunctionsAgreeWithEachOther()
        {
            var authority = Buffer(6);
            p.Memory.Write8(authority + 5, 5);
            var slot = Buffer(4);
            Assert.Equal(1u, A("AllocateAndInitializeSid", authority, 2, 32, 544, 0, 0, 0, 0, 0, 0, slot));
            var admins = p.Memory.Read32(slot);
            Assert.Equal("S-1-5-32-544", SidText(admins));
            Assert.Equal(16u, A("GetLengthSid", admins));
            Assert.Equal(16u, A("GetSidLengthRequired", 2));
            Assert.Equal(1u, A("EqualSid", admins, SidFromText("S-1-5-32-544")));
            Assert.Equal(0u, A("EqualSid", admins, SidFromText("S-1-5-32-545")));
            Assert.Equal(p.Memory.Read32(admins + 12), p.Memory.Read32(A("GetSidSubAuthority", admins, 1)));
            Assert.Equal(2, p.Memory.Read8(A("GetSidSubAuthorityCount", admins)));

            // CopySid needs room, and the copy is equal.
            var small = Buffer(8);
            Assert.Equal(0u, A("CopySid", 8, small, admins));
            Assert.Equal(122u, LastError());
            var copy = Buffer(16);
            Assert.Equal(1u, A("CopySid", 16, copy, admins));
            Assert.Equal(1u, A("EqualSid", admins, copy));
            Assert.Equal(0u, A("FreeSid", admins));

            // Nine sub-authorities do not fit an AllocateAndInitializeSid.
            Assert.Equal(0u, A("AllocateAndInitializeSid", authority, 9, 1, 2, 3, 4, 5, 6, 7, 8, slot));
            Assert.Equal(87u, LastError());

            // InitializeSid builds in place.
            var raw = Buffer(16);
            Assert.Equal(1u, A("InitializeSid", raw, authority, 2));
            p.Memory.Write32(A("GetSidSubAuthority", raw, 0), 32);
            p.Memory.Write32(A("GetSidSubAuthority", raw, 1), 545);
            Assert.Equal("S-1-5-32-545", SidText(raw));
        }

        [Fact]
        public void InvalidSidsAreRefused()
        {
            var bad = Buffer(8);
            p.Memory.Write8(bad, 2);   // revision 2 does not exist
            Assert.Equal(0u, A("IsValidSid", bad));
            Assert.Equal(0u, A("IsValidSid", 0));
            var tooLong = Buffer(8);
            p.Memory.Write8(tooLong, 1);
            p.Memory.Write8(tooLong + 1, 16);   // at most 15 sub-authorities
            Assert.Equal(0u, A("IsValidSid", tooLong));
            Assert.Equal(0u, A("ConvertSidToStringSidW", bad, Buffer(4)));
            Assert.Equal(1337u, LastError());
        }

        [Fact]
        public void WellKnownSidsAndMembership()
        {
            var size = Buffer(4);
            p.Memory.Write32(size, 4);
            var sid = Buffer(32);
            Assert.Equal(0u, A("CreateWellKnownSid", 26, 0, sid, size));   // WinBuiltinAdministratorsSid, too small
            Assert.Equal(122u, LastError());
            Assert.Equal(16u, p.Memory.Read32(size));
            Assert.Equal(1u, A("CreateWellKnownSid", 26, 0, sid, size));
            Assert.Equal("S-1-5-32-544", SidText(sid));
            Assert.Equal(1u, A("IsWellKnownSid", sid, 26));
            Assert.Equal(0u, A("IsWellKnownSid", sid, 27));

            var member = Buffer(4);
            // An ordinary user is in Everyone and Users, and only deny-only in Administrators.
            Assert.Equal(1u, A("CheckTokenMembership", 0, SidFromText("S-1-1-0"), member));
            Assert.Equal(1u, p.Memory.Read32(member));
            Assert.Equal(1u, A("CheckTokenMembership", 0, SidFromText("S-1-5-32-545"), member));
            Assert.Equal(1u, p.Memory.Read32(member));
            Assert.Equal(1u, A("CheckTokenMembership", 0, SidFromText(UserText), member));
            Assert.Equal(1u, p.Memory.Read32(member));
            Assert.Equal(1u, A("CheckTokenMembership", 0, sid, member));
            Assert.Equal(0u, p.Memory.Read32(member));
        }

        [Fact]
        public void TokenGroupsPointIntoTheirOwnBuffer()
        {
            var data = TokenInfo(Token(), 2, out var size);
            var count = p.Memory.Read32(data);
            Assert.True(count >= 5);
            var seenUsers = false;
            for (var n = 0u; n < count; n++)
            {
                var sid = p.Memory.Read32(data + 4 + 8 * n);
                Assert.InRange(sid, data + 4 + 8 * count, data + size - 1);
                Assert.Equal(1u, A("IsValidSid", sid));
                if (SidText(sid) == "S-1-5-32-545") seenUsers = true;
            }
            Assert.True(seenUsers);
        }

        [Fact]
        public void TokenOwnerPrimaryGroupAndIntegrityLevel()
        {
            var token = Token();
            Assert.Equal(UserText, SidText(p.Memory.Read32(TokenInfo(token, 4, out _))));
            var group = p.Memory.Read32(TokenInfo(token, 5, out _));
            Assert.Equal("S-1-5-21-1004336348-1177238915-682003330-513", SidText(group));
            var label = TokenInfo(token, 25, out _);
            Assert.Equal("S-1-16-8192", SidText(p.Memory.Read32(label)));   // medium integrity
            Assert.Equal(0x60u, p.Memory.Read32(label + 4));
        }

        [Fact]
        public void TokenScalarClasses()
        {
            var token = Token();
            Assert.Equal(1u, p.Memory.Read32(TokenInfo(token, 8, out _)));    // TokenType: primary
            Assert.Equal(0u, p.Memory.Read32(TokenInfo(token, 20, out _)));   // TokenElevation: not elevated
            Assert.Equal(1u, p.Memory.Read32(TokenInfo(token, 18, out _)));   // TokenElevationTypeDefault
            Assert.Equal(1u, p.Memory.Read32(TokenInfo(token, 12, out _)));   // TokenSessionId
            // An unknown class is a parameter error, and a bad handle a handle error.
            Assert.Equal(0u, A("GetTokenInformation", token, 999, Buffer(), 512, Buffer(4)));
            Assert.Equal(87u, LastError());
            Assert.Equal(0u, A("GetTokenInformation", 0x1234, 1, Buffer(), 512, Buffer(4)));
            Assert.Equal(6u, LastError());
            // Impersonation level exists only on impersonation tokens.
            Assert.Equal(0u, A("GetTokenInformation", token, 9, Buffer(), 512, Buffer(4)));
        }

        [Fact]
        public void PrivilegesAreLookedUpListedAndAdjusted()
        {
            var luid = Buffer(8);
            Assert.Equal(1u, A("LookupPrivilegeValueW", 0, WideStr("SeShutdownPrivilege"), luid));
            Assert.Equal(19u, p.Memory.Read32(luid));
            Assert.Equal(0u, A("LookupPrivilegeValueW", 0, WideStr("SeNoSuchPrivilege"), luid));
            Assert.Equal(1313u, LastError());

            var name = Buffer(64);
            var length = Buffer(4);
            p.Memory.Write32(length, 64);
            Assert.Equal(1u, A("LookupPrivilegeNameW", 0, luid, name, length));
            Assert.Equal("SeShutdownPrivilege", p.Memory.ReadUnicode(name));
            Assert.Equal(19u, p.Memory.Read32(length));

            var token = Token();
            var list = TokenInfo(token, 3, out _);
            Assert.Equal(5u, p.Memory.Read32(list));
            uint AttributesOf(uint id)
            {
                var data = TokenInfo(token, 3, out _);
                for (var n = 0u; n < p.Memory.Read32(data); n++)
                    if (p.Memory.Read32(data + 4 + 12 * n) == id) return p.Memory.Read32(data + 4 + 12 * n + 8);
                return 0xFFFFFFFF;
            }
            Assert.Equal(0u, AttributesOf(19));   // SeShutdown starts disabled

            var state = Buffer(16);
            p.Memory.Write32(state, 1);
            p.Memory.Write32(state + 4, 19);
            p.Memory.Write32(state + 12, 2);      // SE_PRIVILEGE_ENABLED
            Assert.Equal(1u, A("AdjustTokenPrivileges", token, 0, state, 0, 0, 0));
            Assert.Equal(0u, LastError());
            Assert.Equal(2u, AttributesOf(19));

            // A privilege the token never had: the call succeeds but reports ERROR_NOT_ALL_ASSIGNED.
            p.Memory.Write32(state + 4, 20);      // SeDebugPrivilege
            Assert.Equal(1u, A("AdjustTokenPrivileges", token, 0, state, 0, 0, 0));
            Assert.Equal(1300u, LastError());

            Assert.Equal(1u, A("AdjustTokenPrivileges", token, 1, 0, 0, 0, 0));   // disable all
            Assert.Equal(0u, AttributesOf(19));
            Assert.Equal(0u, AttributesOf(23) & 2);
        }

        [Fact]
        public void DuplicatedTokenIsAnImpersonationToken()
        {
            var token = Token();
            var slot = Buffer(4);
            Assert.Equal(1u, A("DuplicateToken", token, 2, slot));
            var copy = p.Memory.Read32(slot);
            Assert.Equal(2u, p.Memory.Read32(TokenInfo(copy, 8, out _)));
            Assert.Equal(2u, p.Memory.Read32(TokenInfo(copy, 9, out _)));   // SecurityImpersonation
            Assert.Equal(1u, p.Call(p.Imports.Bind("kernel32.dll", "CloseHandle", -1), out var closed, 1_000_000, copy).Ok ? closed : 0);
            Assert.Equal(0u, A("GetTokenInformation", copy, 1, Buffer(), 512, Buffer(4)));   // closed: no longer a token
            Assert.Equal(0u, A("OpenThreadToken", 0xFFFFFFFE, 8, 1, slot));
            Assert.Equal(1008u, LastError());   // ERROR_NO_TOKEN: the thread does not impersonate
        }

        [Fact]
        public void AccountLookupRoundTripsTheUserSid()
        {
            var sid = SidFromText(UserText);
            var name = Buffer(64);
            var domain = Buffer(64);
            var nameLen = Buffer(4);
            var domainLen = Buffer(4);
            var use = Buffer(4);
            p.Memory.Write32(nameLen, 2);
            p.Memory.Write32(domainLen, 64);
            Assert.Equal(0u, A("LookupAccountSidW", 0, sid, name, nameLen, domain, domainLen, use));
            Assert.Equal(122u, LastError());
            Assert.Equal(7u, p.Memory.Read32(nameLen));   // "Player" plus the NUL
            p.Memory.Write32(nameLen, 64);
            Assert.Equal(1u, A("LookupAccountSidW", 0, sid, name, nameLen, domain, domainLen, use));
            Assert.Equal("Player", p.Memory.ReadUnicode(name));
            Assert.Equal("XBOX", p.Memory.ReadUnicode(domain));
            Assert.Equal(1u, p.Memory.Read32(use));       // SidTypeUser

            // And the other way: name to SID gives the same bytes.
            var back = Buffer(64);
            var backLen = Buffer(4);
            p.Memory.Write32(backLen, 64);
            p.Memory.Write32(domainLen, 64);
            Assert.Equal(1u, A("LookupAccountNameW", 0, WideStr("Player"), back, backLen, domain, domainLen, use));
            Assert.Equal(28u, p.Memory.Read32(backLen));
            Assert.Equal(1u, A("EqualSid", sid, back));
            Assert.Equal("XBOX", p.Memory.ReadUnicode(domain));

            p.Memory.Write32(nameLen, 64);
            p.Memory.Write32(domainLen, 64);
            Assert.Equal(1u, A("LookupAccountSidW", 0, SidFromText("S-1-5-32-544"), name, nameLen, domain, domainLen, use));
            Assert.Equal("Administrators", p.Memory.ReadUnicode(name));
            Assert.Equal("BUILTIN", p.Memory.ReadUnicode(domain));
            Assert.Equal(4u, p.Memory.Read32(use));       // SidTypeAlias

            p.Memory.Write32(nameLen, 64);
            p.Memory.Write32(domainLen, 64);
            Assert.Equal(0u, A("LookupAccountSidW", 0, SidFromText("S-1-5-21-1-2-3-9999"), name, nameLen, domain, domainLen, use));
            Assert.Equal(1332u, LastError());             // ERROR_NONE_MAPPED
        }

        [Fact]
        public void SecurityDescriptorHoldsItsPartsAndFlags()
        {
            var sd = Buffer(32);
            Assert.Equal(0u, A("InitializeSecurityDescriptor", sd, 7));
            Assert.Equal(1305u, LastError());   // ERROR_UNKNOWN_REVISION
            Assert.Equal(1u, A("InitializeSecurityDescriptor", sd, 1));
            Assert.Equal(1u, A("IsValidSecurityDescriptor", sd));
            Assert.Equal(1, p.Memory.Read8(sd));

            var acl = Buffer(64);
            Assert.Equal(1u, A("InitializeAcl", acl, 64, 2));
            Assert.Equal(1u, A("SetSecurityDescriptorDacl", sd, 1, acl, 0));
            var present = Buffer(4);
            var got = Buffer(4);
            var defaulted = Buffer(4);
            Assert.Equal(1u, A("GetSecurityDescriptorDacl", sd, present, got, defaulted));
            Assert.Equal(1u, p.Memory.Read32(present));
            Assert.Equal(acl, p.Memory.Read32(got));
            Assert.Equal(0u, p.Memory.Read32(defaulted));
            var control = Buffer(2);
            Assert.Equal(1u, A("GetSecurityDescriptorControl", sd, control, Buffer(4)));
            Assert.Equal(0x4, p.Memory.Read16(control));   // SE_DACL_PRESENT

            // A present, NULL DACL (everyone allowed) is not the same as no DACL.
            Assert.Equal(1u, A("SetSecurityDescriptorDacl", sd, 1, 0, 1));
            Assert.Equal(1u, A("GetSecurityDescriptorDacl", sd, present, got, defaulted));
            Assert.Equal(1u, p.Memory.Read32(present));
            Assert.Equal(0u, p.Memory.Read32(got));
            Assert.Equal(1u, p.Memory.Read32(defaulted));
            Assert.Equal(1u, A("SetSecurityDescriptorDacl", sd, 0, 0, 0));
            Assert.Equal(1u, A("GetSecurityDescriptorDacl", sd, present, got, defaulted));
            Assert.Equal(0u, p.Memory.Read32(present));

            var owner = SidFromText(UserText);
            Assert.Equal(1u, A("SetSecurityDescriptorOwner", sd, owner, 0));
            Assert.Equal(1u, A("GetSecurityDescriptorOwner", sd, got, defaulted));
            Assert.Equal(owner, p.Memory.Read32(got));
            Assert.Equal(20u + 28u, A("GetSecurityDescriptorLength", sd));
        }

        [Fact]
        public void AclsTakeAcesUntilTheyAreFull()
        {
            var acl = Buffer(64);
            Assert.Equal(0u, A("InitializeAcl", acl, 4, 2));    // smaller than the 8-byte header
            Assert.Equal(0u, A("InitializeAcl", acl, 64, 9));
            Assert.Equal(87u, LastError());
            Assert.Equal(1u, A("InitializeAcl", acl, 48, 2));
            Assert.Equal(1u, A("IsValidAcl", acl));
            var everyone = SidFromText("S-1-1-0");               // 12 bytes, so a 20-byte ACE
            Assert.Equal(1u, A("AddAccessAllowedAce", acl, 2, 0x1F01FF, everyone));
            Assert.Equal(1u, A("AddAccessDeniedAce", acl, 2, 0x2, everyone));
            Assert.Equal(2, p.Memory.Read16(acl + 4));            // two ACEs
            Assert.Equal(0, p.Memory.Read8(acl + 8));             // ACCESS_ALLOWED_ACE_TYPE
            Assert.Equal(20, p.Memory.Read16(acl + 8 + 2));
            Assert.Equal(0x1F01FFu, p.Memory.Read32(acl + 8 + 4));
            Assert.Equal(1, p.Memory.Read8(acl + 8 + 20));        // ACCESS_DENIED_ACE_TYPE, right after
            Assert.Equal("S-1-1-0", SidText(acl + 8 + 8));
            Assert.Equal(0u, A("AddAccessAllowedAce", acl, 2, 1, everyone));   // 8 + 20 + 20 fills the 48 bytes
            Assert.Equal(122u, LastError());
            Assert.Equal(2, p.Memory.Read16(acl + 4));
        }
    }
}
