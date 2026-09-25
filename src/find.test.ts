import { expect, test } from "bun:test";
import { networkHosts } from "./net";

test("a /22 covers four /24 blocks", () => {
  const hosts = networkHosts("192.168.68.117", "255.255.252.0");
  expect(hosts[0]).toBe("192.168.68.1");
  expect(hosts[hosts.length - 1]).toBe("192.168.71.254");
  expect(hosts).toContain("192.168.69.40");
  expect(hosts.length).toBe(1022);
});

test("a /24 stays a /24", () => {
  const hosts = networkHosts("10.0.0.43", "255.255.255.0");
  expect(hosts.length).toBe(254);
  expect(hosts[0]).toBe("10.0.0.1");
});
