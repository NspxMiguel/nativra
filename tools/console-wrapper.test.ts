import { expect, test } from "bun:test";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";

// Exercise the actual scripts against a private copy of the lease path.
test("console wrapper preserves nested leases and refuses other or expired owners", async () => {
  const dir = await mkdtemp(join(tmpdir(), "nativra-console-"));
  const lock = join(dir, "lease");
  try {
    for (const script of ["console-lock.sh", "with-console.sh"]) {
      const source = await Bun.file(join(import.meta.dir, script)).text();
      await writeFile(
        join(dir, script),
        source.replaceAll("/tmp/xbox-console.lock", lock),
      );
    }
    const run = async (args: string[]) => {
      const process = Bun.spawn(
        ["bash", join(dir, "with-console.sh"), ...args],
        {
          env: { ...Bun.env, LOCK_OWNER: "test-session" },
          stdout: "pipe",
          stderr: "pipe",
        },
      );
      return {
        code: await process.exited,
        output: await new Response(process.stdout).text(),
      };
    };
    const since = Math.floor(Date.now() / 1000) - 10;
    const lease = `test-session ${since}\n`;
    await writeFile(lock, lease);
    expect(
      (
        await run([
          "bash",
          "-c",
          `bash '${join(dir, "with-console.sh")}' printf nested`,
        ])
      ).output,
    ).toBe("nested");
    expect(await readFile(lock, "utf8")).toBe(lease);
    await writeFile(lock, `another-session ${since}\n`);
    expect((await run(["printf", "unsafe"])).code).toBe(75);
    expect(await readFile(lock, "utf8")).toBe(`another-session ${since}\n`);
    await writeFile(lock, `test-session ${since - 900}\n`);
    expect((await run(["printf", "expired"])).code).toBe(75);
    await rm(lock);
    expect((await run(["printf", "owned"])).output).toBe("owned");
    expect(await Bun.file(lock).exists()).toBe(false);
  } finally {
    await rm(dir, { recursive: true, force: true });
  }
});
