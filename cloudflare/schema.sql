CREATE TABLE IF NOT EXISTS locations (
  location_id TEXT PRIMARY KEY,
  kiosk_json TEXT NOT NULL DEFAULT '[]',
  advertisement_updated_utc TEXT,
  local_last_seen_utc TEXT,
  updated_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS commands (
  command_id TEXT PRIMARY KEY,
  location_id TEXT NOT NULL,
  station_id TEXT NOT NULL,
  command_json TEXT NOT NULL,
  created_utc TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS commands_location_created
  ON commands(location_id, created_utc);
