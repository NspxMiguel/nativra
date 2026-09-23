import { afterEach, expect, mock, spyOn, test } from "bun:test";
import { CmClient, SteamError } from "./cm";
import { connect } from "./download";

afterEach(() => mock.restore());

function fakeConnections() {
  spyOn(CmClient, "endpoints").mockResolvedValue([
    "first.example:443",
    "second.example:443",
    "third.example:443",
  ]);
  const open = spyOn(CmClient.prototype, "connect").mockResolvedValue();
  const close = spyOn(CmClient.prototype, "close").mockImplementation(() => {});
  return { open, close };
}

for (const result of [5, 15, 84]) {
  test(`logon refusal ${result} does not trigger more authentication attempts`, async () => {
    const { open, close } = fakeConnections();
    const refusal = new SteamError("logon refused", result);
    const logon = spyOn(CmClient.prototype, "logOn").mockRejectedValue(refusal);

    await expect(connect("76561197960265728", "test-token")).rejects.toBe(
      refusal,
    );

    expect(open).toHaveBeenCalledTimes(1);
    expect(logon).toHaveBeenCalledTimes(1);
    expect(close).toHaveBeenCalledTimes(1);
  });
}

test("an unavailable connection manager still permits endpoint failover", async () => {
  const { open, close } = fakeConnections();
  spyOn(CmClient.prototype, "logOn")
    .mockRejectedValueOnce(new SteamError("service unavailable", 20))
    .mockResolvedValueOnce();

  const client = await connect("76561197960265728", "test-token");

  expect(open).toHaveBeenCalledTimes(2);
  expect(close).toHaveBeenCalledTimes(1);
  expect(client).toBeInstanceOf(CmClient);
  client.close();
});
