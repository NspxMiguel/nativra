import { expect, test } from "bun:test";
import { DevicePortal, PortalError } from "./portal";

function fixture(items: Array<Record<string, unknown>>) {
  const portal = new DevicePortal({ host: "unused", port: 11443 });
  const requests: string[] = [];
  portal.listFiles = async () => items;
  portal.request = async (method, path) => {
    requests.push(`${method} ${path}`);
    return new Response(null, { status: 204 });
  };
  return { portal, requests };
}

test("file deletion refuses directories including LocalState before DELETE", async () => {
  const { portal, requests } = fixture([{ Name: "LocalState", Type: 16 }]);
  await expect(portal.deleteFile("package", "localstate")).rejects.toThrow(
    PortalError,
  );
  expect(requests).toEqual([]);
});

test("file deletion rejects paths and ignores absent markers", async () => {
  const { portal, requests } = fixture([]);
  for (const name of [
    "",
    ".",
    "..",
    "LocalState/autoplay.txt",
    "LocalState\\autoplay.txt",
  ]) {
    await expect(portal.deleteFile("package", name)).rejects.toThrow(
      PortalError,
    );
  }
  await portal.deleteFile("package", "autodownload.txt", "LocalState");
  expect(requests).toEqual([]);
});

test("file deletion allows empty files and preserves the remote directory", async () => {
  const { portal, requests } = fixture([
    { Name: "autoplay.txt", Type: 0, SizeInBytes: 0 },
  ]);
  await portal.deleteFile("package", "autoplay.txt", "LocalState");
  expect(requests).toHaveLength(1);
  const query = new URLSearchParams(requests[0].split("?")[1]);
  expect(query.get("filename")).toBe("autoplay.txt");
  expect(query.get("path")).toBe("/LocalState");
});
