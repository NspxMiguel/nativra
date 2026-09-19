// His own Steam collections — Favorites, Hidden and whatever he made himself —
// plus the family library. The collections live in the account's cloud config
// store, which only the client connection serves; the family library is a plain
// Web API call.

import { Writer, num, str, list } from "./proto";
import type { CmClient } from "./cm";

export type Collection = {
  id: string;
  name: string;
  /** Explicit members. A dynamic collection has none and carries filters. */
  added: number[];
  removed: number[];
};

const USER_NAMESPACE = 1;
const PREFIX = "user-collections.";

/**
 * The entry's value is field 3, not the 4 the published schema suggests —
 * measured against the account rather than assumed.
 */
export async function collections(cm: CmClient): Promise<Collection[]> {
  const namespace = new Writer().uint(1, USER_NAMESPACE).uint(2, 0);
  const response = await cm.service(
    "CloudConfigStore.Download#1",
    new Writer().message(1, namespace).finish(),
  );

  const out: Collection[] = [];
  for (const data of list(response, 1)) {
    for (const entry of list(data, 3)) {
      const key = str(entry, 1) ?? "";
      if (!key.startsWith(PREFIX)) continue;
      const value = str(entry, 3);
      if (!value) continue;
      try {
        const parsed = JSON.parse(value) as Partial<Collection>;
        if (!parsed.id) continue;
        out.push({
          id: parsed.id,
          name: parsed.name ?? parsed.id,
          added: parsed.added ?? [],
          removed: parsed.removed ?? [],
        });
      } catch {
        // A collection we cannot read is one collection missing, not a failure.
      }
    }
  }
  return out;
}

export type FamilyApp = { appid: number; name: string; ownerSteamIds: string[] };

/**
 * Games shared with him through a Steam family. They show in his client and
 * are not in GetOwnedGames, which is why the library looked short.
 */
export async function familyApps(
  accessToken: string,
  steamId: string,
): Promise<FamilyApp[]> {
  const base = "https://api.steampowered.com/IFamilyGroupsService";
  const forUser = new URLSearchParams({ access_token: accessToken, steamid: steamId });
  const group = (await (
    await fetch(`${base}/GetFamilyGroupForUser/v1/?${forUser}`)
  ).json()) as { response?: { family_groupid?: string } };

  const familyId = group.response?.family_groupid;
  if (!familyId) return [];

  const query = new URLSearchParams({
    access_token: accessToken,
    family_groupid: familyId,
    include_own: "true",
    include_excluded: "true",
    include_free: "true",
  });
  const shared = (await (
    await fetch(`${base}/GetSharedLibraryApps/v1/?${query}`)
  ).json()) as {
    response?: {
      apps?: Array<{ appid: number; name?: string; owner_steamids?: string[] }>;
    };
  };

  return (shared.response?.apps ?? []).map((app) => ({
    appid: app.appid,
    name: app.name ?? String(app.appid),
    ownerSteamIds: app.owner_steamids ?? [],
  }));
}
