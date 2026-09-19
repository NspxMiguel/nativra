// Xbox Device Portal client (WDP REST API over HTTPS :11443).
// The console serves a self-signed certificate and demands a CSRF token that is
// handed out as a cookie on the first GET — both are handled here.

export type PortalConfig = {
  host: string;
  port: number;
  user?: string;
  pass?: string;
};

export type InstalledPackage = {
  Name: string;
  PackageFamilyName: string;
  PackageFullName: string;
  PackageRelativeId: string;
  Version?: { Major: number; Minor: number; Build: number; Revision: number };
};

export class PortalError extends Error {
  constructor(
    message: string,
    readonly status?: number,
    readonly body?: string,
  ) {
    super(message);
    this.name = "PortalError";
  }
}

export class DevicePortal {
  private csrfToken: string | null = null;
  private cookie: string | null = null;

  constructor(private readonly config: PortalConfig) {}

  get base(): string {
    return `https://${this.config.host}:${this.config.port}`;
  }

  private authHeader(): Record<string, string> {
    if (!this.config.user) return {};
    const raw = `${this.config.user}:${this.config.pass ?? ""}`;
    return { Authorization: `Basic ${Buffer.from(raw).toString("base64")}` };
  }

  /** The console rejects non-GET calls without the CSRF token from a prior GET. */
  private async ensureCsrf(): Promise<void> {
    if (this.csrfToken) return;
    const res = await this.raw("GET", "/api/os/machinename");
    const setCookie = res.headers.get("set-cookie") ?? "";
    const match = setCookie.match(/CSRF-Token=([^;,\s]+)/i);
    if (match) {
      this.csrfToken = match[1];
      this.cookie = `CSRF-Token=${match[1]}`;
    }
  }

  private async raw(
    method: string,
    path: string,
    init: RequestInit = {},
  ): Promise<Response> {
    const headers: Record<string, string> = {
      ...this.authHeader(),
      ...((init.headers as Record<string, string>) ?? {}),
    };
    if (this.cookie) headers["Cookie"] = this.cookie;
    if (this.csrfToken && method !== "GET") headers["X-CSRF-Token"] = this.csrfToken;

    return fetch(`${this.base}${path}`, {
      ...init,
      method,
      headers,
      // Dev Mode consoles always present a self-signed certificate.
      tls: { rejectUnauthorized: false },
    } as RequestInit);
  }

  async request(
    method: string,
    path: string,
    init: RequestInit = {},
  ): Promise<Response> {
    if (method !== "GET") await this.ensureCsrf();
    const res = await this.raw(method, path, init);
    if (res.status === 401) {
      throw new PortalError("unauthorized", 401);
    }
    return res;
  }

  private async json<T>(path: string): Promise<T> {
    const res = await this.request("GET", path);
    if (!res.ok) {
      throw new PortalError(`GET ${path} failed`, res.status, await res.text());
    }
    return (await res.json()) as T;
  }

  async machineName(): Promise<string> {
    const data = await this.json<{ ComputerName: string }>("/api/os/machinename");
    return data.ComputerName;
  }

  async osInfo(): Promise<Record<string, unknown>> {
    return this.json("/api/os/info");
  }

  /** Xbox-specific: console type, dev mode state, OS version. */
  async xboxInfo(): Promise<Record<string, unknown>> {
    return this.json("/ext/xbox/info");
  }

  async systemPerf(): Promise<Record<string, unknown>> {
    return this.json("/api/resourcemanager/systemperf");
  }

  async packages(): Promise<InstalledPackage[]> {
    const data = await this.json<{ InstalledPackages: InstalledPackage[] }>(
      "/api/app/packagemanager/packages",
    );
    return data.InstalledPackages ?? [];
  }

  async settings(): Promise<Array<Record<string, string>>> {
    const data = await this.json<{ Settings: Array<Record<string, string>> }>(
      "/ext/settings",
    );
    return data.Settings ?? [];
  }

  async setSetting(name: string, value: string): Promise<void> {
    const res = await this.request("PUT", `/ext/settings/${encodeURIComponent(name)}`, {
      body: JSON.stringify({ Value: value }),
      headers: { "Content-Type": "application/json" },
    });
    if (!res.ok) {
      throw new PortalError(`setting ${name} refused`, res.status, await res.text());
    }
  }

  /**
   * Upload and install an app package plus its dependencies and certificate.
   * The main package goes first; the console installs asynchronously, so the
   * caller polls installState().
   */
  async installPackage(files: string[]): Promise<void> {
    if (files.length === 0) throw new PortalError("no files to install");
    const main = files[0];
    const mainName = main.split("/").pop()!;

    const form = new FormData();
    for (const path of files) {
      const name = path.split("/").pop()!;
      form.append(name, Bun.file(path), name);
    }

    const res = await this.request(
      "POST",
      `/api/app/packagemanager/package?package=${encodeURIComponent(mainName)}`,
      { body: form },
    );

    if (res.status !== 200 && res.status !== 202) {
      throw new PortalError(
        `install of ${mainName} refused`,
        res.status,
        await res.text(),
      );
    }
  }

  async installState(): Promise<{ done: boolean; message: string; code: number }> {
    const res = await this.request("GET", "/api/app/packagemanager/state");
    const text = await res.text();
    // 200 with an empty body means "nothing in flight" — i.e. the last install finished.
    if (res.status === 200 && text.trim().length === 0) {
      return { done: true, message: "", code: 0 };
    }
    let parsed: Record<string, unknown> = {};
    try {
      parsed = JSON.parse(text);
    } catch {
      return { done: res.status === 200, message: text.slice(0, 200), code: res.status };
    }
    const code = Number(parsed.Code ?? 0);
    const reason = String(parsed.Reason ?? parsed.CodeText ?? "");
    // Code 0 / "Succeeded" means the install completed.
    return {
      done: res.status === 200 && code === 0,
      message: reason,
      code,
    };
  }

  async waitForInstall(timeoutMs = 10 * 60 * 1000, onTick?: (msg: string) => void) {
    const started = Date.now();
    while (Date.now() - started < timeoutMs) {
      await Bun.sleep(2000);
      const state = await this.installState();
      if (onTick && state.message) onTick(state.message);
      if (state.done) return;
      if (state.code !== 0 && state.code !== 3805991009) {
        throw new PortalError(state.message || "install failed", state.code);
      }
    }
    throw new PortalError("install timed out");
  }

  async launch(pkg: InstalledPackage): Promise<void> {
    const appid = Buffer.from(pkg.PackageRelativeId).toString("base64");
    const pkgName = Buffer.from(pkg.PackageFullName).toString("base64");
    const res = await this.request(
      "POST",
      `/api/taskmanager/app?appid=${encodeURIComponent(appid)}&package=${encodeURIComponent(pkgName)}`,
    );
    if (!res.ok) {
      throw new PortalError("launch failed", res.status, await res.text());
    }
  }

  async terminate(pkg: InstalledPackage): Promise<void> {
    const pkgName = Buffer.from(pkg.PackageFullName).toString("base64");
    const res = await this.request(
      "DELETE",
      `/api/taskmanager/app?package=${encodeURIComponent(pkgName)}`,
    );
    if (!res.ok) {
      throw new PortalError("terminate failed", res.status, await res.text());
    }
  }

  async uninstall(pkg: InstalledPackage): Promise<void> {
    const res = await this.request(
      "DELETE",
      `/api/app/packagemanager/package?package=${encodeURIComponent(pkg.PackageFullName)}`,
    );
    if (!res.ok) {
      throw new PortalError("uninstall failed", res.status, await res.text());
    }
  }

  // ------------------------------------------------------------ file system
  // Each sideloaded app exposes its own LocalState folder, which is how an
  // emulator gets configured without touching the console by hand.

  async listFiles(
    packageFullName: string,
    path = "",
    knownFolderId = "LocalAppData",
  ): Promise<Array<Record<string, unknown>>> {
    const query = new URLSearchParams({
      knownfolderid: knownFolderId,
      packagefullname: packageFullName,
      path,
    });
    const res = await this.request("GET", `/api/filesystem/apps/files?${query}`);
    if (!res.ok) {
      throw new PortalError("listing failed", res.status, await res.text());
    }
    const data = (await res.json()) as { Items?: Array<Record<string, unknown>> };
    return data.Items ?? [];
  }

  async pushFile(
    packageFullName: string,
    localPath: string,
    remoteDir = "",
    knownFolderId = "LocalAppData",
  ): Promise<void> {
    const name = localPath.split("/").pop()!;
    const query = new URLSearchParams({
      knownfolderid: knownFolderId,
      packagefullname: packageFullName,
      path: remoteDir,
    });
    const form = new FormData();
    form.append(name, Bun.file(localPath), name);
    const res = await this.request("POST", `/api/filesystem/apps/file?${query}`, {
      body: form,
    });
    if (!res.ok) {
      throw new PortalError(`upload of ${name} failed`, res.status, await res.text());
    }
  }

  async pullFile(
    packageFullName: string,
    fileName: string,
    remoteDir = "",
    knownFolderId = "LocalAppData",
  ): Promise<ArrayBuffer> {
    const query = new URLSearchParams({
      knownfolderid: knownFolderId,
      packagefullname: packageFullName,
      filename: fileName,
      path: remoteDir,
    });
    const res = await this.request("GET", `/api/filesystem/apps/file?${query}`);
    if (!res.ok) {
      throw new PortalError(`download of ${fileName} failed`, res.status, await res.text());
    }
    return res.arrayBuffer();
  }

  async screenshot(): Promise<ArrayBuffer> {
    const res = await this.request("GET", "/ext/screenshot");
    if (!res.ok) {
      throw new PortalError("screenshot failed", res.status, await res.text());
    }
    return res.arrayBuffer();
  }
}

/** Probe a single host for an answering Device Portal. */
export async function probe(host: string, port = 11443, timeoutMs = 1500) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutMs);
  try {
    const res = await fetch(`https://${host}:${port}/api/os/machinename`, {
      signal: controller.signal,
      tls: { rejectUnauthorized: false },
    } as RequestInit);
    // 401 still proves a Device Portal is listening — it just wants credentials.
    return res.status === 200 || res.status === 401;
  } catch {
    return false;
  } finally {
    clearTimeout(timer);
  }
}
