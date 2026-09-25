/**
 * Every address on the Mac's own network, from its real mask. Home routers
 * hand out /22 and wider; assuming /24 missed a console that had moved to
 * 192.168.69.x after a reboot.
 */
export function networkHosts(address: string, mask: string, limit = 4094): string[] {
  const toInt = (dotted: string) =>
    dotted.split(".").reduce((n, part) => ((n << 8) | (Number(part) & 255)) >>> 0, 0);
  const toDotted = (n: number) => [24, 16, 8, 0].map((shift) => (n >>> shift) & 255).join(".");
  const ip = toInt(address);
  const bits = toInt(mask);
  const network = (ip & bits) >>> 0;
  const size = Math.min(((~bits) >>> 0) + 1, limit + 2);
  const hosts: string[] = [];
  for (let offset = 1; offset < size - 1; offset++) hosts.push(toDotted((network + offset) >>> 0));
  return hosts;
}
