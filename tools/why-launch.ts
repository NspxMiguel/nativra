#!/usr/bin/env bun
// Why a launch was refused. The client reports the failure without the body,
// and the body is where the console explains itself.
import { DevicePortal } from "../src/portal";

// The same place the command line keeps it: the keychain, never a file.
const stored = Bun.spawnSync([
  "security", "find-generic-password", "-s", "claude-autonomous:XBDEV", "-w",
]);
const config = JSON.parse(stored.stdout.toString().trim());
const portal = new DevicePortal(config);
const apps = await portal.packages();
const app = apps.find((one) =>
  (one.PackageFullName ?? "").toLowerCase().includes("kiosk"),
);
if (!app) {
  console.log("no package with that name is installed");
  process.exit(1);
}
console.log("full name  :", app.PackageFullName);
console.log("family     :", app.PackageFamilyName);
console.log("relative id:", (app as Record<string, unknown>).PackageRelativeId);

// The launch itself, with whatever the console says when it refuses.
const id = String((app as Record<string, unknown>).PackageRelativeId ?? "");
const query = new URLSearchParams({
  appid: Buffer.from(id).toString("base64"),
  package: Buffer.from(app.PackageFullName ?? "").toString("base64"),
});
const answer = await (portal as unknown as {
  request(method: string, path: string): Promise<Response>;
}).request("POST", `/api/taskmanager/app?${query}`);
console.log("launch   :", answer.status, answer.statusText);
console.log("body     :", (await answer.text()).slice(0, 600));
