const json = (value, status = 200) => new Response(JSON.stringify(value), {
  status,
  headers: { "content-type": "application/json; charset=utf-8", "cache-control": "no-store" }
});

const authorized = (request, env) => {
  const expected = `Bearer ${env.RELAY_ACCESS_KEY || ""}`;
  return expected.length > 20 && request.headers.get("authorization") === expected;
};

const validLocation = value => typeof value === "string" && /^[A-Za-z0-9_-]{3,80}$/.test(value);

async function getLocation(env, id) {
  return await env.DB.prepare(
    "SELECT kiosk_json, advertisement_updated_utc FROM locations WHERE location_id = ?"
  ).bind(id).first();
}

async function getAds(env, id) {
  const object = await env.ADS.get(`locations/${id}/advertisements.json`);
  return object ? await object.json() : null;
}

async function saveAdsIfNewer(env, id, advertisements, updatedUtc) {
  if (!advertisements || !updatedUtc) return;
  const current = await getLocation(env, id);
  if (current?.advertisement_updated_utc &&
      Date.parse(current.advertisement_updated_utc) >= Date.parse(updatedUtc)) return;
  await env.ADS.put(`locations/${id}/advertisements.json`, JSON.stringify(advertisements), {
    httpMetadata: { contentType: "application/json" }
  });
  await env.DB.prepare(
    "UPDATE locations SET advertisement_updated_utc = ?, updated_utc = ? WHERE location_id = ?"
  ).bind(updatedUtc, new Date().toISOString(), id).run();
}

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (!authorized(request, env)) return json({ error: "Unauthorized" }, 401);
    if (request.method === "GET" && url.pathname === "/api/health")
      return json({ ok: true, service: "Mullet Hop secure relay", timeUtc: new Date().toISOString() });
    if (request.method !== "POST" || url.pathname !== "/api/sync")
      return json({ error: "Not found" }, 404);

    let body;
    try { body = await request.json(); }
    catch { return json({ error: "Invalid JSON" }, 400); }
    if (!validLocation(body.locationId) || !["local", "remote"].includes(body.role))
      return json({ error: "Invalid sync request" }, 400);

    const id = body.locationId;
    const now = new Date().toISOString();
    await env.DB.prepare(
      "INSERT INTO locations(location_id, kiosk_json, updated_utc) VALUES(?, '[]', ?) ON CONFLICT(location_id) DO NOTHING"
    ).bind(id, now).run();
    await saveAdsIfNewer(env, id, body.advertisements, body.advertisementUpdatedUtc);

    if (body.role === "local") {
      await env.DB.prepare(
        "UPDATE locations SET kiosk_json = ?, local_last_seen_utc = ?, updated_utc = ? WHERE location_id = ?"
      ).bind(JSON.stringify(body.kiosks || []), now, now, id).run();
      const rows = await env.DB.prepare(
        "SELECT command_id, station_id, command_json FROM commands WHERE location_id = ? ORDER BY created_utc LIMIT 200"
      ).bind(id).all();
      const commands = (rows.results || []).map(row => ({
        stationId: row.station_id,
        command: JSON.parse(row.command_json)
      }));
      if (rows.results?.length) {
        const ids = rows.results.map(row => row.command_id);
        await env.DB.prepare(
          `DELETE FROM commands WHERE command_id IN (${ids.map(() => "?").join(",")})`
        ).bind(...ids).run();
      }
      const location = await getLocation(env, id);
      return json({
        kiosks: body.kiosks || [], commands,
        advertisements: await getAds(env, id),
        advertisementUpdatedUtc: location?.advertisement_updated_utc || null
      });
    }

    for (const item of body.commands || []) {
      if (!item?.stationId || !item?.command?.id) continue;
      await env.DB.prepare(
        "INSERT OR IGNORE INTO commands(command_id, location_id, station_id, command_json, created_utc) VALUES(?, ?, ?, ?, ?)"
      ).bind(item.command.id, id, item.stationId, JSON.stringify(item.command), now).run();
    }
    const location = await getLocation(env, id);
    return json({
      kiosks: JSON.parse(location?.kiosk_json || "[]"), commands: [],
      advertisements: await getAds(env, id),
      advertisementUpdatedUtc: location?.advertisement_updated_utc || null
    });
  }
};
