using System;
using System.Collections.Generic;
using System.Text;
using Nativra.X86.Cpu;

namespace Nativra.X86.Loader
{
    // advapi32's security family: SIDs in their binary layout, the process
    // token (one local user, its groups, privileges, integrity level),
    // account lookup, and the absolute security descriptors and ACLs a
    // program builds before creating a kernel object. There is one identity
    // for the whole guest, so everything a program derives from it (a token
    // query, a LookupAccountSid round trip, a string SID) agrees.
    public sealed partial class GuestKernel
    {
        private const uint ErrorNoToken = 1008, ErrorNotAllAssigned = 1300,
            ErrorInvalidSid = 1337, ErrorUnknownRevision = 1305, ErrorNoSuchPrivilege = 1313,
            ErrorNoneMapped = 1332, ErrorInvalidAcl = 1336, ErrorInvalidSecurityDescr = 1338;
        private const uint SeGroupEnabledDefault = 7, SeGroupUseForDenyOnly = 0x10;   // MANDATORY | ENABLED_BY_DEFAULT | ENABLED

        // The one user: a local account under a machine SID, RID 1001 (the first one Windows hands out).
        private static readonly byte[] UserSid = MakeSid(5, 21, 1004336348, 1177238915, 682003330, 1001);
        private static readonly byte[] PrimaryGroupSid = MakeSid(5, 21, 1004336348, 1177238915, 682003330, 513);
        private static readonly byte[] DomainSid = MakeSid(5, 21, 1004336348, 1177238915, 682003330);
        private const string AccountName = "Player", AccountDomain = "XBOX";

        /// <summary>The groups a standard interactive user is in, with the attributes Windows gives them.</summary>
        private static readonly KeyValuePair<byte[], uint>[] TokenGroupList =
        {
            new KeyValuePair<byte[], uint>(MakeSid(1, 0), SeGroupEnabledDefault),                 // Everyone
            new KeyValuePair<byte[], uint>(MakeSid(5, 32, 545), SeGroupEnabledDefault),              // BUILTIN\Users
            new KeyValuePair<byte[], uint>(MakeSid(5, 4), SeGroupEnabledDefault),                    // INTERACTIVE
            new KeyValuePair<byte[], uint>(MakeSid(5, 11), SeGroupEnabledDefault),                   // Authenticated Users
            new KeyValuePair<byte[], uint>(MakeSid(5, 15), SeGroupEnabledDefault),                   // This Organization
            new KeyValuePair<byte[], uint>(MakeSid(2, 0), SeGroupEnabledDefault),                    // LOCAL
            new KeyValuePair<byte[], uint>(MakeSid(5, 32, 544), SeGroupUseForDenyOnly),   // BUILTIN\Administrators: deny-only, as in a filtered token
        };

        private static readonly Dictionary<string, uint> PrivilegeLuids = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
        {
            ["SeCreateTokenPrivilege"] = 2, ["SeAssignPrimaryTokenPrivilege"] = 3, ["SeLockMemoryPrivilege"] = 4,
            ["SeIncreaseQuotaPrivilege"] = 5, ["SeTcbPrivilege"] = 7, ["SeSecurityPrivilege"] = 8,
            ["SeTakeOwnershipPrivilege"] = 9, ["SeLoadDriverPrivilege"] = 10, ["SeSystemProfilePrivilege"] = 11,
            ["SeSystemtimePrivilege"] = 12, ["SeProfileSingleProcessPrivilege"] = 13,
            ["SeIncreaseBasePriorityPrivilege"] = 14, ["SeCreatePagefilePrivilege"] = 15,
            ["SeCreatePermanentPrivilege"] = 16, ["SeBackupPrivilege"] = 17, ["SeRestorePrivilege"] = 18,
            ["SeShutdownPrivilege"] = 19, ["SeDebugPrivilege"] = 20, ["SeAuditPrivilege"] = 21,
            ["SeSystemEnvironmentPrivilege"] = 22, ["SeChangeNotifyPrivilege"] = 23,
            ["SeRemoteShutdownPrivilege"] = 24, ["SeUndockPrivilege"] = 25, ["SeManageVolumePrivilege"] = 28,
            ["SeImpersonatePrivilege"] = 29, ["SeCreateGlobalPrivilege"] = 30,
            ["SeIncreaseWorkingSetPrivilege"] = 33, ["SeTimeZonePrivilege"] = 34,
            ["SeCreateSymbolicLinkPrivilege"] = 35,
        };

        /// <summary>A standard user's privileges, each as luid -> attributes (SE_PRIVILEGE_ENABLED = 2, ENABLED_BY_DEFAULT = 1).</summary>
        private static Dictionary<uint, uint> DefaultPrivileges() => new Dictionary<uint, uint>
        {
            [19] = 0, [23] = 3, [25] = 0, [33] = 0, [34] = 0,
        };

        private sealed class GuestToken
        {
            public Dictionary<uint, uint> Privileges = DefaultPrivileges();
            public bool Impersonation;
        }

        private readonly Dictionary<uint, GuestToken> tokens = new Dictionary<uint, GuestToken>();

        // --- SID values -------------------------------------------------------------------------

        /// <summary>A SID in its binary layout: revision 1, sub-authority count, a 48-bit big-endian authority, then little-endian sub-authorities.</summary>
        internal static byte[] MakeSid(ulong authority, params uint[] subAuthorities)
        {
            var sid = new byte[8 + 4 * subAuthorities.Length];
            sid[0] = 1;
            sid[1] = (byte)subAuthorities.Length;
            for (var n = 0; n < 6; n++) sid[2 + n] = (byte)(authority >> (8 * (5 - n)));
            for (var n = 0; n < subAuthorities.Length; n++) BitConverter.GetBytes(subAuthorities[n]).CopyTo(sid, 8 + 4 * n);
            return sid;
        }

        /// <summary>The SID's text form, S-R-I-S1-S2... (the authority is decimal below 2^32, else hex).</summary>
        internal static string SidToString(byte[] sid)
        {
            ulong authority = 0;
            for (var n = 0; n < 6; n++) authority = authority << 8 | sid[2 + n];
            var text = new StringBuilder("S-").Append(sid[0]).Append('-');
            text.Append(authority < 0x100000000UL ? authority.ToString() : "0x" + authority.ToString("X12"));
            for (var n = 0; n < sid[1]; n++) text.Append('-').Append(BitConverter.ToUInt32(sid, 8 + 4 * n));
            return text.ToString();
        }

        /// <summary>Parses the text form back (also the S-1-5-32-544 style ones), or null if it is not a SID.</summary>
        internal static byte[] ParseSid(string text)
        {
            if (text == null) return null;
            var parts = text.Trim().Split('-');
            if (parts.Length < 3 || parts.Length > 3 + 15 || !string.Equals(parts[0], "S", StringComparison.OrdinalIgnoreCase) || parts[1] != "1") return null;
            ulong authority;
            if (parts[2].StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (!ulong.TryParse(parts[2].Substring(2), System.Globalization.NumberStyles.HexNumber, null, out authority) || authority >> 48 != 0) return null;
            }
            else if (!uint.TryParse(parts[2], out var small)) return null;
            else authority = small;
            var subs = new uint[parts.Length - 3];
            for (var n = 0; n < subs.Length; n++)
                if (!uint.TryParse(parts[3 + n], out subs[n])) return null;
            return MakeSid(authority, subs);
        }

        /// <summary>Reads a SID out of guest memory; null for a null pointer, a revision other than 1 or more than 15 sub-authorities.</summary>
        private byte[] ReadSid(uint address)
        {
            if (address == 0) return null;
            var head = memory.ReadBytes(address, 8);
            if (head[0] != 1 || head[1] > 15) return null;
            return memory.ReadBytes(address, 8 + 4 * head[1]);
        }

        private uint WriteSidCopy(byte[] sid)
        {
            var at = heap.Alloc((uint)sid.Length);
            memory.WriteBytes(at, sid);
            return at;
        }

        private static bool SameSid(byte[] a, byte[] b) => a != null && b != null && SamePrefix(a, b, a.Length) && a.Length == b.Length;

        private static bool SamePrefix(byte[] a, byte[] b, int count)
        {
            if (a.Length < count || b.Length < count) return false;
            for (var n = 0; n < count; n++)
                if (a[n] != b[n]) return false;
            return true;
        }

        // --- tokens -----------------------------------------------------------------------------

        private GuestToken TokenOf(uint handle)
        {
            if (handle == 0 && tokens.Count == 0) return null;
            return tokens.TryGetValue(handle, out var token) ? token : null;
        }

        private bool IsMemberOfToken(byte[] sid)
        {
            if (SameSid(sid, UserSid)) return true;
            foreach (var group in TokenGroupList)
                if (SameSid(sid, group.Key) && (group.Value & 0x4) != 0) return true;   // SE_GROUP_ENABLED only
            return false;
        }

        /// <summary>
        /// A token class's data with its SID pointers still as offsets inside it:
        /// <paramref name="fixups"/> lists the positions of the pointers that must be
        /// rebased on the caller's buffer address (they are 32-bit guest pointers).
        /// </summary>
        private byte[] TokenData(GuestToken token, uint cls, List<int> fixups)
        {
            switch (cls)
            {
                case 1:   // TokenUser: SID_AND_ATTRIBUTES, then the SID itself
                    return WithTrailingSids(new[] { new KeyValuePair<byte[], uint>(UserSid, 0) }, false, fixups);
                case 2:   // TokenGroups: count, then the array
                    return WithTrailingSids(TokenGroupList, true, fixups);
                case 3:   // TokenPrivileges
                {
                    var data = new byte[4 + 12 * token.Privileges.Count];
                    BitConverter.GetBytes(token.Privileges.Count).CopyTo(data, 0);
                    var at = 4;
                    foreach (var p in token.Privileges)
                    {
                        BitConverter.GetBytes(p.Key).CopyTo(data, at);
                        BitConverter.GetBytes(p.Value).CopyTo(data, at + 8);
                        at += 12;
                    }
                    return data;
                }
                case 4: return WithTrailingSids(new[] { new KeyValuePair<byte[], uint>(UserSid, 0) }, false, fixups, noAttributes: true);   // TokenOwner
                case 5: return WithTrailingSids(new[] { new KeyValuePair<byte[], uint>(PrimaryGroupSid, 0) }, false, fixups, noAttributes: true);   // TokenPrimaryGroup
                case 8: return BitConverter.GetBytes(token.Impersonation ? 2u : 1u);                // TokenType
                case 9: return token.Impersonation ? BitConverter.GetBytes(2u) : null;               // TokenImpersonationLevel: only on impersonation tokens
                case 12: return BitConverter.GetBytes(1u);                                           // TokenSessionId
                case 17: return BitConverter.GetBytes(0u);                                           // TokenSandBoxInert
                case 18: return BitConverter.GetBytes(1u);                                           // TokenElevationType: TokenElevationTypeDefault
                case 20: return BitConverter.GetBytes(0u);                                           // TokenElevation
                case 25:  // TokenIntegrityLevel: TOKEN_MANDATORY_LABEL, Medium
                    return WithTrailingSids(new[] { new KeyValuePair<byte[], uint>(MakeSid(0x10, 0x2000), 0x60) }, false, fixups);
                case 29: return BitConverter.GetBytes(0u);                                           // TokenIsAppContainer
                default: return null;
            }
        }

        /// <summary>A header (optionally a count) plus SID_AND_ATTRIBUTES entries, with the SIDs stored after them.</summary>
        private static byte[] WithTrailingSids(KeyValuePair<byte[], uint>[] entries, bool counted, List<int> fixups, bool noAttributes = false)
        {
            var entrySize = noAttributes ? 4 : 8;
            var head = (counted ? 4 : 0) + entrySize * entries.Length;
            var total = head;
            foreach (var e in entries) total += (e.Key.Length + 3) & ~3;
            var data = new byte[total];
            if (counted) BitConverter.GetBytes(entries.Length).CopyTo(data, 0);
            var entryAt = counted ? 4 : 0;
            var sidAt = head;
            foreach (var e in entries)
            {
                BitConverter.GetBytes(sidAt).CopyTo(data, entryAt);   // an offset until the caller's address is added
                fixups.Add(entryAt);
                if (!noAttributes) BitConverter.GetBytes(e.Value).CopyTo(data, entryAt + 4);
                e.Key.CopyTo(data, sidAt);
                entryAt += entrySize;
                sidAt += (e.Key.Length + 3) & ~3;
            }
            return data;
        }

        private uint GetTokenInformation(uint handle, uint cls, uint buffer, uint length, uint returned)
        {
            var token = TokenOf(handle);
            if (token == null) { process.LastError = ErrorInvalidHandle; return 0; }
            var fixups = new List<int>();
            var data = TokenData(token, cls, fixups);
            if (data == null) { process.LastError = ErrorInvalidParameter; return 0; }
            if (returned == 0) { process.LastError = ErrorInvalidParameter; return 0; }
            memory.Write32(returned, (uint)data.Length);
            if (length < data.Length || buffer == 0) { process.LastError = ErrorInsufficientBuffer; return 0; }
            foreach (var at in fixups) BitConverter.GetBytes(BitConverter.ToUInt32(data, at) + buffer).CopyTo(data, at);
            memory.WriteBytes(buffer, data);
            return 1;
        }

        private uint AdjustPrivileges(uint handle, uint disableAll, uint newState, uint bufferLength, uint previous, uint returnLength)
        {
            var token = TokenOf(handle);
            if (token == null) { process.LastError = ErrorInvalidHandle; return 0; }
            var before = new List<KeyValuePair<uint, uint>>();
            var missing = false;
            if (disableAll != 0)
            {
                foreach (var luid in new List<uint>(token.Privileges.Keys))
                {
                    if ((token.Privileges[luid] & 2) != 0) before.Add(new KeyValuePair<uint, uint>(luid, token.Privileges[luid]));
                    token.Privileges[luid] &= ~2u;
                }
            }
            else if (newState != 0)
            {
                var count = memory.Read32(newState);
                if (count > 0xFFFF) { process.LastError = ErrorInvalidParameter; return 0; }
                for (var n = 0u; n < count; n++)
                {
                    var luid = memory.Read32(newState + 4 + 12 * n);
                    var attributes = memory.Read32(newState + 4 + 12 * n + 8);
                    if (!token.Privileges.TryGetValue(luid, out var current)) { missing = true; continue; }
                    before.Add(new KeyValuePair<uint, uint>(luid, current));
                    // SE_PRIVILEGE_REMOVED (4) is the only way to lose one for good.
                    if ((attributes & 4) != 0) token.Privileges.Remove(luid);
                    else token.Privileges[luid] = (current & ~2u) | (attributes & 2);
                }
            }
            if (previous != 0)
            {
                var need = 4u + 12u * (uint)before.Count;
                if (bufferLength < need) { memory.Write32(returnLength, need); process.LastError = ErrorInsufficientBuffer; return 0; }
                memory.Write32(previous, (uint)before.Count);
                var at = previous + 4;
                foreach (var b in before)
                {
                    memory.Write32(at, b.Key); memory.Write32(at + 4, 0); memory.Write32(at + 8, b.Value);
                    at += 12;
                }
            }
            if (returnLength != 0) memory.Write32(returnLength, previous != 0 ? 4u + 12u * (uint)before.Count : 0);
            process.LastError = missing ? ErrorNotAllAssigned : 0;   // success, but not for every privilege asked
            return 1;
        }

        // --- well-known SIDs and account names -------------------------------------------------

        private static byte[] WellKnownSid(uint type, byte[] domain)
        {
            switch (type)
            {
                case 0: return MakeSid(0, 0);                    // WinNullSid
                case 1: return MakeSid(1, 0);                    // WinWorldSid (Everyone)
                case 2: return MakeSid(2, 0);                    // WinLocalSid
                case 3: return MakeSid(3, 0);                    // WinCreatorOwnerSid
                case 4: return MakeSid(3, 1);                    // WinCreatorGroupSid
                case 7: return MakeSid(5);                       // WinNtAuthoritySid
                case 9: return MakeSid(5, 2);                    // WinNetworkSid
                case 10: return MakeSid(5, 3);                   // WinBatchSid
                case 11: return MakeSid(5, 4);                   // WinInteractiveSid
                case 12: return MakeSid(5, 6);                   // WinServiceSid
                case 13: return MakeSid(5, 7);                   // WinAnonymousSid
                case 17: return MakeSid(5, 11);                  // WinAuthenticatedUserSid
                case 22: return MakeSid(5, 18);                  // WinLocalSystemSid
                case 23: return MakeSid(5, 19);                  // WinLocalServiceSid
                case 24: return MakeSid(5, 20);                  // WinNetworkServiceSid
                case 25: return MakeSid(5, 32);                  // WinBuiltinDomainSid
                case 26: return MakeSid(5, 32, 544);             // WinBuiltinAdministratorsSid
                case 27: return MakeSid(5, 32, 545);             // WinBuiltinUsersSid
                case 28: return MakeSid(5, 32, 546);             // WinBuiltinGuestsSid
                case 29: return MakeSid(5, 32, 547);             // WinBuiltinPowerUsersSid
                case 38: return domain != null ? WithRid(domain, 500) : null;   // WinAccountAdministratorSid
                case 39: return domain != null ? WithRid(domain, 501) : null;   // WinAccountGuestSid
                case 41: return domain != null ? WithRid(domain, 513) : null;   // WinAccountDomainUsersSid
                default: return null;
            }
        }

        private static byte[] WithRid(byte[] domain, uint rid)
        {
            var subs = new uint[domain[1] + 1];
            for (var n = 0; n < domain[1]; n++) subs[n] = BitConverter.ToUInt32(domain, 8 + 4 * n);
            subs[subs.Length - 1] = rid;
            var authority = new byte[6];
            Array.Copy(domain, 2, authority, 0, 6);
            var sid = MakeSid(0, subs);
            Array.Copy(authority, 0, sid, 2, 6);
            return sid;
        }

        /// <summary>The account a SID names: (name, domain, SID_NAME_USE) or null when it is not one this machine knows.</summary>
        private static Tuple<string, string, uint> AccountOfSid(byte[] sid)
        {
            if (SameSid(sid, UserSid)) return Tuple.Create(AccountName, AccountDomain, 1u);                       // SidTypeUser
            if (SameSid(sid, PrimaryGroupSid)) return Tuple.Create("None", AccountDomain, 2u);                   // SidTypeGroup
            if (SameSid(sid, DomainSid)) return Tuple.Create(AccountDomain, AccountDomain, 3u);                  // SidTypeDomain
            if (SameSid(sid, MakeSid(1, 0))) return Tuple.Create("Everyone", "", 5u);                            // SidTypeWellKnownGroup
            if (SameSid(sid, MakeSid(5, 18))) return Tuple.Create("SYSTEM", "NT AUTHORITY", 5u);
            if (SameSid(sid, MakeSid(5, 19))) return Tuple.Create("LOCAL SERVICE", "NT AUTHORITY", 5u);
            if (SameSid(sid, MakeSid(5, 20))) return Tuple.Create("NETWORK SERVICE", "NT AUTHORITY", 5u);
            if (SameSid(sid, MakeSid(5, 4))) return Tuple.Create("INTERACTIVE", "NT AUTHORITY", 5u);
            if (SameSid(sid, MakeSid(5, 11))) return Tuple.Create("Authenticated Users", "NT AUTHORITY", 5u);
            if (SameSid(sid, MakeSid(5, 32, 544))) return Tuple.Create("Administrators", "BUILTIN", 4u);          // SidTypeAlias
            if (SameSid(sid, MakeSid(5, 32, 545))) return Tuple.Create("Users", "BUILTIN", 4u);
            if (SameSid(sid, MakeSid(5, 32, 546))) return Tuple.Create("Guests", "BUILTIN", 4u);
            if (SameSid(sid, MakeSid(5, 32))) return Tuple.Create("BUILTIN", "BUILTIN", 3u);
            return null;
        }

        private static byte[] SidOfAccount(string name, out string domain, out uint use)
        {
            var slash = name.IndexOf('\\');
            var user = slash >= 0 ? name.Substring(slash + 1) : name;
            var scope = slash >= 0 ? name.Substring(0, slash) : "";
            domain = AccountDomain; use = 1;
            if (user.Length == 0 && scope.Length != 0) user = scope;
            if (string.Equals(user, AccountName, StringComparison.OrdinalIgnoreCase) && (scope.Length == 0 || scope == "." || string.Equals(scope, AccountDomain, StringComparison.OrdinalIgnoreCase))) return UserSid;
            foreach (var candidate in new[] { MakeSid(1, 0), MakeSid(5, 18), MakeSid(5, 19), MakeSid(5, 20), MakeSid(5, 4), MakeSid(5, 11), MakeSid(5, 32, 544), MakeSid(5, 32, 545), MakeSid(5, 32, 546) })
            {
                var account = AccountOfSid(candidate);
                if (!string.Equals(account.Item1, user, StringComparison.OrdinalIgnoreCase)) continue;
                if (scope.Length != 0 && !string.Equals(account.Item2, scope, StringComparison.OrdinalIgnoreCase)) continue;
                domain = account.Item2; use = account.Item3;
                return candidate;
            }
            return null;
        }

        /// <summary>
        /// Text for a LookupAccount* out buffer pair. Both sizes are in/out: when
        /// either buffer is short, both report what they need including the NUL
        /// and nothing is written; on success they report the length without it.
        /// </summary>
        private bool PutLookupTexts(string first, uint firstBuffer, uint firstSize, string second, uint secondBuffer, uint secondSize, bool wide)
        {
            var fits = memory.Read32(firstSize) >= first.Length + 1 && memory.Read32(secondSize) >= second.Length + 1;
            if (!fits)
            {
                memory.Write32(firstSize, (uint)first.Length + 1);
                memory.Write32(secondSize, (uint)second.Length + 1);
                return false;
            }
            if (firstBuffer != 0) WriteText(firstBuffer, first, wide);
            if (secondBuffer != 0) WriteText(secondBuffer, second, wide);
            memory.Write32(firstSize, (uint)first.Length);
            memory.Write32(secondSize, (uint)second.Length);
            return true;
        }

        // --- the installed functions ----------------------------------------------------------

        private void InstallSecurity(GuestImports i)
        {
            const string a = "advapi32.dll";

            // Tokens.
            i.Register(a, "OpenProcessToken", CallConv.Stdcall, 3, c =>
            {
                if (c.Arg(0) != PseudoCurrentProcess) { process.LastError = ErrorInvalidHandle; return 0; }
                var handle = NewHandle();
                tokens[handle] = new GuestToken();
                memory.Write32(c.Arg(2), handle);
                return 1;
            });
            i.Register(a, "OpenThreadToken", CallConv.Stdcall, 4, c => { process.LastError = ErrorNoToken; return 0; });
            i.Register(a, "GetTokenInformation", CallConv.Stdcall, 5, c => GetTokenInformation(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3), c.Arg(4)));
            i.Register(a, "DuplicateToken", CallConv.Stdcall, 3, c => DuplicateToken(c.Arg(0), 2, c.Arg(2)));
            i.Register(a, "DuplicateTokenEx", CallConv.Stdcall, 6, c => DuplicateToken(c.Arg(0), c.Arg(4) == 1 ? 1u : 2u, c.Arg(5)));
            i.Register(a, "AdjustTokenPrivileges", CallConv.Stdcall, 6, c => AdjustPrivileges(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3), c.Arg(4), c.Arg(5)));
            i.Register(a, "CheckTokenMembership", CallConv.Stdcall, 3, c =>
            {
                var sid = ReadSid(c.Arg(1));
                if (sid == null) { process.LastError = ErrorInvalidSid; return 0; }
                if (c.Arg(0) != 0 && TokenOf(c.Arg(0)) == null) { process.LastError = ErrorInvalidHandle; return 0; }
                memory.Write32(c.Arg(2), IsMemberOfToken(sid) ? 1u : 0u);
                return 1;
            });
            i.Register(a, "ImpersonateSelf", CallConv.Stdcall, 1, c => 1);
            i.Register(a, "RevertToSelf", CallConv.Stdcall, 0, c => 1);

            // Privileges.
            foreach (var wide in new[] { false, true })
            {
                var w = wide;
                var s = wide ? "W" : "A";
                i.Register(a, "LookupPrivilegeValue" + s, CallConv.Stdcall, 3, c =>
                {
                    if (!PrivilegeLuids.TryGetValue(ReadText(c.Arg(1), w), out var luid)) { process.LastError = ErrorNoSuchPrivilege; return 0; }
                    memory.Write32(c.Arg(2), luid);
                    memory.Write32(c.Arg(2) + 4, 0);
                    return 1;
                });
                i.Register(a, "LookupPrivilegeName" + s, CallConv.Stdcall, 4, c =>
                {
                    var luid = memory.Read32(c.Arg(1));
                    foreach (var p in PrivilegeLuids)
                        if (p.Value == luid && memory.Read32(c.Arg(1) + 4) == 0) return PrivilegeNameInto(p.Key, c.Arg(2), c.Arg(3), w);
                    return Fail(ErrorNoSuchPrivilege);
                });
                i.Register(a, "LookupAccountSid" + s, CallConv.Stdcall, 7, c =>
                {
                    var sid = ReadSid(c.Arg(1));
                    if (sid == null) return Fail(ErrorInvalidSid);
                    var account = AccountOfSid(sid);
                    if (account == null) return Fail(ErrorNoneMapped);
                    if (!PutLookupTexts(account.Item1, c.Arg(2), c.Arg(3), account.Item2, c.Arg(4), c.Arg(5), w)) return Fail(ErrorInsufficientBuffer);
                    memory.Write32(c.Arg(6), account.Item3);
                    return 1;
                });
                i.Register(a, "LookupAccountName" + s, CallConv.Stdcall, 7, c =>
                {
                    var sid = SidOfAccount(ReadText(c.Arg(1), w), out var domain, out var use);
                    if (sid == null) return Fail(ErrorNoneMapped);
                    var size = memory.Read32(c.Arg(3));
                    memory.Write32(c.Arg(3), (uint)sid.Length);
                    var domainSize = memory.Read32(c.Arg(5));
                    if (size < sid.Length || domainSize < domain.Length + 1)
                    {
                        memory.Write32(c.Arg(5), (uint)domain.Length + 1);
                        return Fail(ErrorInsufficientBuffer);
                    }
                    memory.WriteBytes(c.Arg(2), sid);
                    WriteText(c.Arg(4), domain, w);
                    memory.Write32(c.Arg(5), (uint)domain.Length);
                    memory.Write32(c.Arg(6), use);
                    return 1;
                });
                i.Register(a, "ConvertSidToStringSid" + s, CallConv.Stdcall, 2, c =>
                {
                    var sid = ReadSid(c.Arg(0));
                    if (sid == null) return Fail(ErrorInvalidSid);
                    var text = SidToString(sid);
                    var at = heap.Alloc((uint)(text.Length + 1) * (w ? 2u : 1u));   // the caller frees it with LocalFree
                    WriteText(at, text, w);
                    memory.Write32(c.Arg(1), at);
                    return 1;
                });
                i.Register(a, "ConvertStringSidToSid" + s, CallConv.Stdcall, 2, c =>
                {
                    var sid = ParseSid(ReadText(c.Arg(0), w));
                    if (sid == null) return Fail(ErrorInvalidSid);
                    memory.Write32(c.Arg(1), WriteSidCopy(sid));
                    return 1;
                });
            }

            // SIDs.
            i.Register(a, "IsValidSid", CallConv.Stdcall, 1, c => ReadSid(c.Arg(0)) != null ? 1u : 0u);
            i.Register(a, "GetLengthSid", CallConv.Stdcall, 1, c => 8 + 4u * memory.Read8(c.Arg(0) + 1));
            i.Register(a, "GetSidLengthRequired", CallConv.Stdcall, 1, c => 8 + 4 * c.Arg(0));
            i.Register(a, "GetSidIdentifierAuthority", CallConv.Stdcall, 1, c => c.Arg(0) + 2);
            i.Register(a, "GetSidSubAuthorityCount", CallConv.Stdcall, 1, c => c.Arg(0) + 1);
            i.Register(a, "GetSidSubAuthority", CallConv.Stdcall, 2, c => c.Arg(0) + 8 + 4 * c.Arg(1));
            i.Register(a, "EqualSid", CallConv.Stdcall, 2, c =>
            {
                var x = ReadSid(c.Arg(0));
                var y = ReadSid(c.Arg(1));
                if (x == null || y == null) return Fail(ErrorInvalidSid);
                process.LastError = 0;
                return SameSid(x, y) ? 1u : 0u;
            });
            i.Register(a, "EqualPrefixSid", CallConv.Stdcall, 2, c =>
            {
                var x = ReadSid(c.Arg(0));
                var y = ReadSid(c.Arg(1));
                if (x == null || y == null) return Fail(ErrorInvalidSid);
                if (x[1] != y[1]) return 0;
                return SamePrefix(x, y, x.Length - 4) ? 1u : 0u;   // all but the last sub-authority
            });
            i.Register(a, "CopySid", CallConv.Stdcall, 3, c =>
            {
                var sid = ReadSid(c.Arg(2));
                if (sid == null) return Fail(ErrorInvalidSid);
                if (c.Arg(0) < sid.Length) return Fail(ErrorInsufficientBuffer);
                memory.WriteBytes(c.Arg(1), sid);
                return 1;
            });
            i.Register(a, "InitializeSid", CallConv.Stdcall, 3, c =>
            {
                var count = c.Arg(2);
                if (count == 0 || count > 15) return Fail(ErrorInvalidParameter);
                var sid = new byte[8 + 4 * count];
                sid[0] = 1; sid[1] = (byte)count;
                memory.ReadBytes(c.Arg(1), 6).CopyTo(sid, 2);
                memory.WriteBytes(c.Arg(0), sid);
                return 1;
            });
            i.Register(a, "AllocateAndInitializeSid", CallConv.Stdcall, 11, c =>
            {
                var count = c.Arg(1);
                if (count == 0 || count > 8) return Fail(ErrorInvalidParameter);
                var sid = new byte[8 + 4 * count];
                sid[0] = 1; sid[1] = (byte)count;
                memory.ReadBytes(c.Arg(0), 6).CopyTo(sid, 2);
                for (var n = 0; n < count; n++) BitConverter.GetBytes(c.Arg(2 + n)).CopyTo(sid, 8 + 4 * n);
                memory.Write32(c.Arg(10), WriteSidCopy(sid));
                return 1;
            });
            i.Register(a, "FreeSid", CallConv.Stdcall, 1, c => { heap.Free(c.Arg(0)); return 0; });
            i.Register(a, "CreateWellKnownSid", CallConv.Stdcall, 4, c =>
            {
                byte[] domain = null;
                if (c.Arg(1) != 0 && (domain = ReadSid(c.Arg(1))) == null) return Fail(ErrorInvalidParameter);
                var sid = WellKnownSid(c.Arg(0), domain);
                if (sid == null) return Fail(ErrorInvalidParameter);
                var size = memory.Read32(c.Arg(3));
                memory.Write32(c.Arg(3), (uint)sid.Length);
                if (size < sid.Length) return Fail(ErrorInsufficientBuffer);
                memory.WriteBytes(c.Arg(2), sid);
                return 1;
            });
            i.Register(a, "IsWellKnownSid", CallConv.Stdcall, 2, c =>
            {
                var sid = ReadSid(c.Arg(0));
                return sid != null && SameSid(sid, WellKnownSid(c.Arg(1), null)) ? 1u : 0u;
            });

            // Security descriptors (absolute, as a program builds them) and ACLs.
            i.Register(a, "InitializeSecurityDescriptor", CallConv.Stdcall, 2, c =>
            {
                if (c.Arg(1) != 1) return Fail(ErrorUnknownRevision);
                memory.WriteBytes(c.Arg(0), new byte[20]);
                memory.Write8(c.Arg(0), 1);
                return 1;
            });
            i.Register(a, "IsValidSecurityDescriptor", CallConv.Stdcall, 1, c => c.Arg(0) != 0 && memory.Read8(c.Arg(0)) == 1 ? 1u : Fail(ErrorInvalidSecurityDescr));
            i.Register(a, "GetSecurityDescriptorLength", CallConv.Stdcall, 1, c => SecurityDescriptorLength(c.Arg(0)));
            i.Register(a, "GetSecurityDescriptorControl", CallConv.Stdcall, 3, c =>
            {
                if (memory.Read8(c.Arg(0)) != 1) return Fail(ErrorUnknownRevision);
                if (c.Arg(1) != 0) memory.Write16(c.Arg(1), memory.Read16(c.Arg(0) + 2));
                if (c.Arg(2) != 0) memory.Write32(c.Arg(2), 1);
                return 1;
            });
            RegisterDescriptorPart(i, "Dacl", 16, 0x4, 0x8);
            RegisterDescriptorPart(i, "Sacl", 12, 0x10, 0x20);
            i.Register(a, "SetSecurityDescriptorOwner", CallConv.Stdcall, 3, c => SetDescriptorSid(c.Arg(0), 4, 0x1, c.Arg(1), c.Arg(2)));
            i.Register(a, "SetSecurityDescriptorGroup", CallConv.Stdcall, 3, c => SetDescriptorSid(c.Arg(0), 8, 0x2, c.Arg(1), c.Arg(2)));
            i.Register(a, "GetSecurityDescriptorOwner", CallConv.Stdcall, 3, c => GetDescriptorSid(c.Arg(0), 4, 0x1, c.Arg(1), c.Arg(2)));
            i.Register(a, "GetSecurityDescriptorGroup", CallConv.Stdcall, 3, c => GetDescriptorSid(c.Arg(0), 8, 0x2, c.Arg(1), c.Arg(2)));
            i.Register(a, "InitializeAcl", CallConv.Stdcall, 3, c =>
            {
                if (c.Arg(2) != 2 && c.Arg(2) != 4) return Fail(ErrorInvalidParameter);
                if (c.Arg(1) < 8 || c.Arg(1) > 0xFFFF) return Fail(ErrorInsufficientBuffer);
                memory.WriteBytes(c.Arg(0), new byte[8]);
                memory.Write8(c.Arg(0), (byte)c.Arg(2));
                memory.Write16(c.Arg(0) + 2, (ushort)(c.Arg(1) & ~3u));
                return 1;
            });
            i.Register(a, "IsValidAcl", CallConv.Stdcall, 1, c => c.Arg(0) != 0 && (memory.Read8(c.Arg(0)) == 2 || memory.Read8(c.Arg(0)) == 4) ? 1u : 0u);
            i.Register(a, "AddAccessAllowedAce", CallConv.Stdcall, 4, c => AddAce(c.Arg(0), c.Arg(1), 0, 0, c.Arg(2), c.Arg(3)));
            i.Register(a, "AddAccessDeniedAce", CallConv.Stdcall, 4, c => AddAce(c.Arg(0), c.Arg(1), 1, 0, c.Arg(2), c.Arg(3)));
            i.Register(a, "AddAccessAllowedAceEx", CallConv.Stdcall, 5, c => AddAce(c.Arg(0), c.Arg(1), 0, (byte)c.Arg(2), c.Arg(3), c.Arg(4)));
            i.Register(a, "AddAccessDeniedAceEx", CallConv.Stdcall, 5, c => AddAce(c.Arg(0), c.Arg(1), 1, (byte)c.Arg(2), c.Arg(3), c.Arg(4)));
        }

        /// <summary>LookupPrivilegeName: the size is the buffer's characters, and on success the name's length without the NUL.</summary>
        private uint PrivilegeNameInto(string name, uint buffer, uint sizePtr, bool wide)
        {
            var size = memory.Read32(sizePtr);
            if (size < name.Length + 1) { memory.Write32(sizePtr, (uint)name.Length + 1); return Fail(ErrorInsufficientBuffer); }
            WriteText(buffer, name, wide);
            memory.Write32(sizePtr, (uint)name.Length);
            return 1;
        }

        private uint DuplicateToken(uint source, uint kind, uint result)
        {
            var original = TokenOf(source);
            if (original == null) return Fail(ErrorInvalidHandle);
            var handle = NewHandle();
            tokens[handle] = new GuestToken { Privileges = new Dictionary<uint, uint>(original.Privileges), Impersonation = kind == 2 };
            memory.Write32(result, handle);
            return 1;
        }

        // The 32-bit SECURITY_DESCRIPTOR: revision, Sbz1, control, then owner, group, SACL and DACL pointers.
        private void RegisterDescriptorPart(GuestImports i, string part, uint pointerOffset, ushort presentBit, ushort defaultedBit)
        {
            const string a = "advapi32.dll";
            i.Register(a, "SetSecurityDescriptor" + part, CallConv.Stdcall, 4, c =>
            {
                var sd = c.Arg(0);
                if (memory.Read8(sd) != 1) return Fail(ErrorUnknownRevision);
                var control = (ushort)(memory.Read16(sd + 2) & ~(presentBit | defaultedBit));
                if (c.Arg(1) != 0)
                {
                    control |= presentBit;
                    if (c.Arg(3) != 0) control |= defaultedBit;
                    memory.Write32(sd + pointerOffset, c.Arg(2));   // a present, NULL ACL grants everyone full access
                }
                else memory.Write32(sd + pointerOffset, 0);
                memory.Write16(sd + 2, control);
                return 1;
            });
            i.Register(a, "GetSecurityDescriptor" + part, CallConv.Stdcall, 4, c =>
            {
                var sd = c.Arg(0);
                if (memory.Read8(sd) != 1) return Fail(ErrorUnknownRevision);
                var control = memory.Read16(sd + 2);
                var present = (control & presentBit) != 0;
                memory.Write32(c.Arg(1), present ? 1u : 0u);
                if (present)
                {
                    memory.Write32(c.Arg(2), memory.Read32(sd + pointerOffset));
                    memory.Write32(c.Arg(3), (control & defaultedBit) != 0 ? 1u : 0u);
                }
                return 1;
            });
        }

        private uint SetDescriptorSid(uint sd, uint offset, ushort defaultedBit, uint sid, uint defaulted)
        {
            if (memory.Read8(sd) != 1) return Fail(ErrorUnknownRevision);
            memory.Write32(sd + offset, sid);
            var control = (ushort)(memory.Read16(sd + 2) & ~defaultedBit);
            if (sid != 0 && defaulted != 0) control |= defaultedBit;
            memory.Write16(sd + 2, control);
            return 1;
        }

        private uint GetDescriptorSid(uint sd, uint offset, ushort defaultedBit, uint sidOut, uint defaultedOut)
        {
            if (memory.Read8(sd) != 1) return Fail(ErrorUnknownRevision);
            memory.Write32(sidOut, memory.Read32(sd + offset));
            memory.Write32(defaultedOut, (memory.Read16(sd + 2) & defaultedBit) != 0 ? 1u : 0u);
            return 1;
        }

        /// <summary>The size an absolute descriptor takes once made self-relative: header plus whatever it points at.</summary>
        private uint SecurityDescriptorLength(uint sd)
        {
            uint total = 20;
            foreach (var offset in new uint[] { 4, 8 })
            {
                var sid = memory.Read32(sd + offset);
                if (sid != 0) total += 8 + 4u * memory.Read8(sid + 1);
            }
            foreach (var offset in new uint[] { 12, 16 })
            {
                var acl = memory.Read32(sd + offset);
                if (acl != 0) total += memory.Read16(acl + 2);
            }
            return total;
        }

        private uint AddAce(uint acl, uint revision, byte type, byte flags, uint mask, uint sidAddress)
        {
            var aclRevision = memory.Read8(acl);
            if (aclRevision != 2 && aclRevision != 4) return Fail(ErrorInvalidAcl);
            if (revision < 2 || revision > 4) return Fail(ErrorUnknownRevision);
            var sid = ReadSid(sidAddress);
            if (sid == null) return Fail(ErrorInvalidSid);
            var size = memory.Read16(acl + 2);
            var count = memory.Read16(acl + 4);
            var end = 8u;
            for (var n = 0; n < count; n++) end += memory.Read16(acl + end + 2);   // each ACE's own size
            var aceSize = (uint)(8 + sid.Length);
            if (end + aceSize > size) return Fail(ErrorInsufficientBuffer);   // ERROR_ALLOTTED_SPACE_EXCEEDED on Windows, same effect: nothing added
            memory.Write8(acl + end, type);
            memory.Write8(acl + end + 1, flags);
            memory.Write16(acl + end + 2, (ushort)aceSize);
            memory.Write32(acl + end + 4, mask);
            memory.WriteBytes(acl + end + 8, sid);
            memory.Write16(acl + 4, (ushort)(count + 1));
            if (aclRevision < 4 && revision > aclRevision) memory.Write8(acl, (byte)Math.Min(revision, 4u));
            return 1;
        }
    }
}
