# Mullet Hop Cloud Relay Setup

Run `Setup-Cloudflare-Relay.cmd` on a trusted Windows computer. The script uses
Cloudflare's official Wrangler tool and browser authorization to create:

- one Worker named `mullet-hop-kiosk-relay`;
- one D1 database named `mullet-hop-kiosk-relay`;
- one R2 bucket named `mullet-hop-kiosk-ads`;
- one randomly generated relay access key stored as a Worker secret.

After deployment, the script displays the relay URL, location ID, and access
key and stores a local copy in `Mullet-Hop-Remote-Connection.json`. Keep that
file private. Enter the values in **Controller Program → Remote Access** on the
on-site controller, save, and restart. Use **Copy Setup Code** there, paste it
into the remote controller, check **This is a remote machine**, then save and
restart the remote controller.

Both controllers make outbound HTTPS connections. Do not configure router port
forwarding. Kiosks continue to communicate only with the on-site controller and
retain their last synchronized advertisements and Business Hours settings if
either controller or the internet connection is unavailable.

# Business Hours sync upgrade

Existing relay installations must apply the one-time database migration before deploying this version:

```powershell
npx wrangler d1 execute mullet-hop-kiosk-relay --remote --file migrate-business-hours.sql
npx wrangler deploy
```
