#!/usr/bin/env bun
// A stand-in for the Xbox Device Portal, faithful to the parts xbdev uses:
// HTTPS with a self-signed certificate, Basic auth, the CSRF cookie/header
// handshake, multipart package upload and the asynchronous install state.
// It exists so the client can be exercised before the console is available.

import { join } from "node:path";

export type FakePortalOptions = {
  port?: number;
  user?: string;
  pass?: string;
  certDir: string;
};

type InstalledPackage = {
  Name: string;
  PackageFamilyName: string;
  PackageFullName: string;
  PackageRelativeId: string;
  Version: { Major: number; Minor: number; Build: number; Revision: number };
};

export type FakePortalState = {
  packages: InstalledPackage[];
  settings: Array<Record<string, string>>;
  launched: string[];
  terminated: string[];
  uploads: Array<{ endpoint: string; files: string[] }>;
  files: Record<string, number>;
  csrfIssued: number;
  rejectedWithoutCsrf: number;
  rejectedWithoutAuth: number;
};

const CSRF_VALUE = "fake-csrf-token-42";

export function startFakePortal(options: FakePortalOptions) {
  const user = options.user ?? "xbox";
  const pass = options.pass ?? "segredo";

  const state: FakePortalState = {
    packages: [
      {
        Name: "RetroArch",
        PackageFamilyName: "RetroArch_8wekyb3d8bbwe",
        PackageFullName: "RetroArch_1.0.0.0_x64__8wekyb3d8bbwe",
        PackageRelativeId: "App",
        Version: { Major: 1, Minor: 0, Build: 0, Revision: 0 },
      },
    ],
    settings: [
      { Name: "TVResolution", Value: "1080p", Type: "Select", Category: "Video" },
      // Measured on a Series X running OS 10.0.26100 — this is the switch that
      // gives a sideloaded app the full GPU, more RAM and sight of the drive.
      {
        Name: "DefaultUWPContentTypeToGame",
        Value: "false",
        Type: "Bool",
        Category: "Preferences",
      },
    ],
    launched: [],
    terminated: [],
    uploads: [],
    files: {},
    csrfIssued: 0,
    rejectedWithoutCsrf: 0,
    rejectedWithoutAuth: 0,
  };

  // Install is asynchronous on the console: the POST returns immediately and
  // the client polls until the state endpoint reports it finished.
  let installFinishesAt = 0;

  const json = (body: unknown, init: ResponseInit = {}) =>
    new Response(JSON.stringify(body), {
      ...init,
      headers: { "Content-Type": "application/json", ...(init.headers ?? {}) },
    });

  const server = Bun.serve({
    port: options.port ?? 0,
    tls: {
      cert: Bun.file(join(options.certDir, "cert.pem")),
      key: Bun.file(join(options.certDir, "key.pem")),
    },
    async fetch(request) {
      const url = new URL(request.url);
      const path = url.pathname;

      const auth = request.headers.get("authorization");
      const expected = `Basic ${Buffer.from(`${user}:${pass}`).toString("base64")}`;
      if (auth !== expected) {
        state.rejectedWithoutAuth++;
        return new Response("unauthorized", { status: 401 });
      }

      if (request.method !== "GET") {
        if (request.headers.get("x-csrf-token") !== CSRF_VALUE) {
          state.rejectedWithoutCsrf++;
          return new Response("csrf required", { status: 403 });
        }
      }

      const headers: Record<string, string> = {};
      if (request.method === "GET" && path === "/api/os/machinename") {
        state.csrfIssued++;
        headers["Set-Cookie"] = `CSRF-Token=${CSRF_VALUE}; Path=/`;
        return json({ ComputerName: "XBOXONE-FAKE" }, { headers });
      }

      if (path === "/ext/xbox/info") {
        return json({
          OsVersion: "10.0.25398.4908",
          DevMode: "Retail",
          ConsoleType: "Xbox Series X",
        });
      }

      if (path === "/api/app/packagemanager/packages") {
        return json({ InstalledPackages: state.packages });
      }

      if (path === "/api/app/packagemanager/package" && request.method === "POST") {
        const form = await request.formData();
        const files = [...form.keys()];
        state.uploads.push({ endpoint: url.searchParams.get("package") ?? "", files });
        const main = files[0] ?? "app.msixbundle";
        const name = main.replace(/[_.].*$/, "");
        state.packages.push({
          Name: name,
          PackageFamilyName: `${name}_fake`,
          PackageFullName: `${name}_1.0.0.0_x64__fake`,
          PackageRelativeId: "App",
          Version: { Major: 1, Minor: 0, Build: 0, Revision: 0 },
        });
        installFinishesAt = Date.now() + 2500;
        return new Response("", { status: 202 });
      }

      if (path === "/api/app/packagemanager/state") {
        if (installFinishesAt && Date.now() < installFinishesAt) {
          return json({ Code: 3805991009, Reason: "Installing", CodeText: "InProgress" });
        }
        return new Response("", { status: 200 });
      }

      if (path === "/api/taskmanager/app" && request.method === "POST") {
        const pkg = url.searchParams.get("package") ?? "";
        state.launched.push(Buffer.from(pkg, "base64").toString());
        return new Response("", { status: 200 });
      }

      if (path === "/api/taskmanager/app" && request.method === "DELETE") {
        const pkg = url.searchParams.get("package") ?? "";
        state.terminated.push(Buffer.from(pkg, "base64").toString());
        return new Response("", { status: 200 });
      }

      if (path === "/ext/settings" && request.method === "GET") {
        return json({ Settings: state.settings });
      }

      if (path.startsWith("/ext/settings/") && request.method === "PUT") {
        const name = decodeURIComponent(path.slice("/ext/settings/".length));
        const body = (await request.json()) as { Value: string };
        const setting = state.settings.find((s) => s.Name === name);
        if (!setting) return new Response("unknown setting", { status: 404 });
        setting.Value = body.Value;
        return new Response("", { status: 200 });
      }

      if (path === "/ext/screenshot") {
        // Smallest valid PNG, so the client writes a real file.
        const png = Buffer.from(
          "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==",
          "base64",
        );
        return new Response(png, { headers: { "Content-Type": "image/png" } });
      }

      if (path === "/api/filesystem/apps/file" && request.method === "POST") {
        const form = await request.formData();
        for (const [name, value] of form.entries()) {
          state.files[name] = value instanceof File ? value.size : 0;
        }
        return new Response("", { status: 200 });
      }

      if (path === "/api/filesystem/apps/files") {
        return json({
          Items: Object.entries(state.files).map(([name, size]) => ({
            Name: name,
            SizeInBytes: size,
            Type: 32,
          })),
        });
      }

      return new Response("not found", { status: 404 });
    },
  });

  return { server, state, port: server.port, user, pass };
}

// Running this file directly starts the stub for manual poking.
if (import.meta.main) {
  const certDir = process.argv[2] ?? "/tmp/xbdev-certs";
  const fake = startFakePortal({ certDir, port: 11443 });
  console.log(`fake Device Portal on https://127.0.0.1:${fake.port}`);
  console.log(`user: ${fake.user}  pass: ${fake.pass}`);
}
