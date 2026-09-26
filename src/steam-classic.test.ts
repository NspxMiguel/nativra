import { expect, test } from "bun:test";

const classic = await Bun.file("uwp/Kiosk/Native/SteamClassic.cs").text();
const bridge = await Bun.file("uwp/Kiosk/Native/SteamBridge.cs").text();
const imports = await Bun.file("uwp/Kiosk/Native/SystemImports.cs").text();
const csproj = await Bun.file("uwp/Kiosk/Kiosk.csproj").text();

test("a statically imported steam_api64 reaches the bridge before anything else", () => {
  // LEGO Jurassic World links steam_api64.dll statically, so its imports are
  // resolved through SystemImports.Resolve at load time and never touch the
  // emulated GetProcAddress path LoaderStubs already bridges for Unity games.
  const method = imports.slice(
    imports.indexOf("public IntPtr Resolve(string module, string function)"),
  );
  const bridgeCheck = method.indexOf("SteamBridge.Serves(module)");
  const overridesCheck = method.indexOf("Overrides.TryGetValue(module");
  expect(bridgeCheck).toBeGreaterThan(-1);
  expect(overridesCheck).toBeGreaterThan(-1);
  expect(bridgeCheck).toBeLessThan(overridesCheck);
  expect(method).toContain("SteamBridge.Resolve(function)");
});

test("the flat resolver falls through to the classic API before caching an unknown export as dead", () => {
  const method = bridge.slice(bridge.indexOf("public static IntPtr Resolve(string export)"));
  const knownCheck = method.indexOf("answers.TryGetValue(export, out var known)");
  const classicCheck = method.indexOf("SteamClassic.Resolve(export)");
  const catchAll = method.indexOf("Answer(export, (a, b, c, d) => IntPtr.Zero)");
  expect(knownCheck).toBeGreaterThan(-1);
  expect(classicCheck).toBeGreaterThan(knownCheck);
  expect(classicCheck).toBeLessThan(catchAll);
  // Answers found through the classic path are cached like every other one.
  expect(method).toContain("answers[export] = classic;");
});

test("the bare classic accessor names are let through the flat name filter", () => {
  const filter = bridge.slice(
    bridge.indexOf('if (!export.StartsWith("SteamAPI_"'),
    bridge.indexOf("lock (answers)"),
  );
  expect(filter).toContain("SteamClassic.IsAccessorName(export)");
  expect(classic).toContain('"SteamUser", "SteamFriends", "SteamApps", "SteamUserStats", "SteamUtils",');
  expect(classic).toContain(
    '"SteamRemoteStorage", "SteamMatchmaking", "SteamNetworking", "SteamScreenshots", "SteamHTTP",',
  );
});

test("SteamClassic answers the flat SteamAPI_ exports the classic loop needs, and the six accessors", () => {
  expect(classic).toContain('case "SteamAPI_RunCallbacks":');
  expect(classic).toContain('case "SteamAPI_RegisterCallback":');
  expect(classic).toContain('case "SteamAPI_UnregisterCallback":');
  // Accessors are exported functions that return the object; returning the
  // object pointer itself made the game execute the vtable pointer.
  for (const [name, make] of [
    ["SteamUser", "User"],
    ["SteamFriends", "Friends"],
    ["SteamApps", "Apps"],
    ["SteamUserStats", "UserStats"],
    ["SteamUtils", "Utils"],
    ["SteamRemoteStorage", "RemoteStorage"],
  ]) {
    expect(classic).toContain(`case "${name}": return Accessor(export, ${make});`);
    expect(classic).not.toContain(`case "${name}": return ${make}();`);
  }
  expect(classic).toContain("Fn call = (a0, a1, a2, a3, a4, a5, a6, a7, a8, a9) => make();");
});

test("GetSteamID writes through the hidden return pointer rather than returning a value directly", () => {
  const build = classic.slice(classic.indexOf("private static IntPtr BuildUser()"), classic.indexOf("private static IntPtr BuildFriends()"));
  // 'result' is argument position two -- right after 'this' -- per the MSVC
  // x64 ABI for a class returned by value, and GetSteamID must write the id
  // into it and hand the same pointer back rather than returning the id.
  const getSteamId = build.slice(build.indexOf("Fn getSteamId ="));
  expect(getSteamId).toContain("Marshal.WriteInt64(result, 0, (long)SteamBridge.SteamId)");
  expect(getSteamId.indexOf("return result;")).toBeGreaterThan(-1);
  expect(classic).toContain("/* 2  GetSteamID                  */ Keep(getSteamId),");
});

test("every hidden-pointer-return friend/clan slot answers through the same shared helper", () => {
  // GetFriendCount is always 0, so these should never actually be exercised,
  // but they still have to answer the ABI shape correctly (write 0, return
  // the pointer) rather than crash or leave the caller's temporary unset.
  const hiddenSlots = [
    "GetFriendByIndex",
    "GetClanByIndex",
    "GetFriendFromSourceByIndex",
    "GetClanOwner",
    "GetClanOfficerByIndex",
    "GetCoplayFriend",
    "GetChatMemberByIndex",
  ];
  for (const name of hiddenSlots) {
    const line = classic.split("\n").find((l) => l.includes(name) && l.includes("hiddenZero"));
    expect(line, `${name} should route through hiddenZero`).toBeTruthy();
  }
  expect(classic).toContain("if (result != IntPtr.Zero) Marshal.WriteInt64(result, 0, 0);");
});

test("BIsSubscribedApp and BIsAppInstalled only ever match the running app's own id", () => {
  const build = classic.slice(classic.indexOf("private static IntPtr BuildApps()"), classic.indexOf("private static IntPtr BuildUserStats()"));
  expect(build).toContain("(uint)appId.ToInt64() == SteamBridge.AppId ? True : Zero;");
  expect((build.match(/== SteamBridge\.AppId \? True : Zero;/g) ?? []).length).toBe(2);
});

test("SetStat's float overload reads the value from its own typed delegate, not the generic Fn shape", () => {
  // A bare float/double argument travels in an XMM register, not the integer
  // registers Fn reads, so it needs the WithFloat shape to get real bits.
  const build = classic.slice(classic.indexOf("private static IntPtr BuildUserStats()"), classic.indexOf("private static IntPtr BuildRemoteStorage()"));
  expect(build).toContain("WithFloat setStatFloat = (self, name, value) =>");
  expect(build).toContain("SteamBridge.FloatStats[Text(name)] = value;");
});

test("GetStat/SetStat land in the MSVC-reversed slot order (float before int32)", () => {
  const build = classic.slice(classic.indexOf("private static IntPtr BuildUserStats()"), classic.indexOf("private static IntPtr BuildRemoteStorage()"));
  const getFloat = build.indexOf("/* 1  GetStat(float*)");
  const getInt = build.indexOf("/* 2  GetStat(int32*)");
  const setFloat = build.indexOf("/* 3  SetStat(float)");
  const setInt = build.indexOf("/* 4  SetStat(int32)");
  expect(getFloat).toBeGreaterThan(-1);
  expect(getFloat).toBeLessThan(getInt);
  expect(setFloat).toBeLessThan(setInt);
});

test("achievements and stats set through the classic vtable share SteamBridge's own storage and save path", () => {
  const build = classic.slice(classic.indexOf("private static IntPtr BuildUserStats()"), classic.indexOf("private static IntPtr BuildRemoteStorage()"));
  expect(build).toContain("SteamBridge.Achieved");
  expect(build).toContain("SteamBridge.Numbers");
  expect(build).toContain("SteamBridge.Save();");
  expect(build).toContain("SteamBridge.StatsReceived()");
  expect(build).toContain("SteamBridge.StatsStored()");
  expect(build).toContain("SteamBridge.AchievementStored(key)");
  // StoreStats -- and only StoreStats -- also pushes to the real Steam sync.
  const storeStats = build.slice(build.indexOf("Fn storeStats ="), build.indexOf("var emptyString"));
  expect(storeStats).toContain("SteamBridge.Changed?.Invoke();");
  const setAchievement = build.slice(build.indexOf("Fn setAchievement ="), build.indexOf("Fn clearAchievement ="));
  expect(setAchievement).not.toContain("Changed?.Invoke()");
});

test("SteamBridge exposes the storage helpers SteamClassic needs instead of duplicating them", () => {
  expect(bridge).toContain("internal static IntPtr Utf8(string text)");
  expect(bridge).toContain("internal static string Text(IntPtr pointer)");
  expect(bridge).toContain("internal static IDictionary<string, float> FloatStats => floatStats;");
  expect(bridge).toContain("internal static byte[] StatsReceived()");
  expect(bridge).toContain("internal static byte[] StatsStored()");
  expect(bridge).toContain("internal static byte[] AchievementStored(string name)");
  expect(bridge).toContain("internal static void Save()");
});

test("RegisterCallback writes the CCallbackBase flags and id at the documented offsets", () => {
  const register = classic.slice(
    classic.indexOf("private static void RegisterCallback"),
    classic.indexOf("private static void UnregisterCallback"),
  );
  expect(register).toContain("Marshal.WriteByte(callback, CallbackFlagsOffset, (byte)(flags | CallbackFlagRegistered));");
  expect(register).toContain("Marshal.WriteInt32(callback, CallbackIdOffset, iCallback);");
  expect(classic).toContain("private const int CallbackFlagsOffset = 8;");
  expect(classic).toContain("private const int CallbackIdOffset = 12;");
});

test("RunCallbacks delivers to vtable slot 1 (the plain Run(void*) overload), not slot 0", () => {
  expect(classic).toContain("private const int RunSlotIndex = 1;");
  const run = classic.slice(classic.indexOf("private static void RunPendingCallbacks"));
  expect(run).toContain("Marshal.ReadIntPtr(vtable, RunSlotIndex * IntPtr.Size)");
  expect(run).toContain("Marshal.GetDelegateForFunctionPointer<RunCallback>(slot)");
});

test("a callback delivery failure does not stop the rest of the batch or crash the game", () => {
  const run = classic.slice(classic.indexOf("private static void RunPendingCallbacks"));
  const tryDeliver = run.slice(run.indexOf("try\n"));
  expect(tryDeliver).toContain("catch");
  expect(tryDeliver.indexOf("catch")).toBeGreaterThan(tryDeliver.indexOf("run(callback, buffer);"));
});

test("polling an API call handle never spins forever: it always answers completed-and-failed", () => {
  const build = classic.slice(classic.indexOf("private static IntPtr BuildUtils()"), classic.indexOf("private static IntPtr BuildApps()"));
  const isCompleted = build.slice(build.indexOf("Fn isCompleted ="), build.indexOf("Fn callResult ="));
  expect(isCompleted).toContain("Marshal.WriteByte(pbFailed, 1);");
  expect(isCompleted).toContain("return True;");
});

test("the classic file is wired into the UWP project", () => {
  expect(csproj).toContain('<Compile Include="Native\\SteamClassic.cs" />');
});

test("SteamClassic never answers before the account is active", () => {
  expect(classic).toContain("if (!SteamBridge.Active || string.IsNullOrEmpty(export)) return IntPtr.Zero;");
});
