# BwbAlarmWatcher2

Alarm monitor service for the Bergwacht Kempten rescue station. Runs on a Raspberry Pi 5,
polls the Bergwacht Bayern API for active alarms and switches the station TV (LG OLED55B6D-Z)
on and off — CEC as the actuator, a WLAN ping as the power-status sensor
(hybrid architecture, see `knowledge/03 Systemarchitektur.md`).

**Stack:** .NET 10 (LTS) Worker Service, C#. Successor of the v1 .NET 9 `BwbAlarmWatcher`.

## Behaviour

1. Polls the GraphQL endpoint (`getAlarms`: `alarms(createdAfter: …, limit: …)
   { hasNextPage results { id extid subkind message } }` — the same wire shape as v1)
   every 15 s (configurable 10–30 s) with a bearer token. Default endpoint is the services
   environment v1 ran against: `https://api.services.bergwacht-bayern.org/graphql`.
2. Applies the v1 filter rules (`Alarm__*` config keys unchanged): manual alarms (`M` in
   extid) only if enabled, alarms without message dropped, blocked message parts
   (default `ECH`) win over required parts.
3. On a **new** alarm (unknown extid, latest state not `CLOSED`):
   - TV answers ping → it was switched on manually → hands off, **no** auto-off timer (FA-6).
   - TV does not answer → `cec-client` sends `on 0` + `as`, a 30-minute auto-off timer starts (FA-4/FA-5).
4. A further alarm during the window extends the timer; after expiry `standby 0` is sent —
   but only if the service itself switched the TV on (`TurnedOnByService`).
5. Every external failure (API, ping, CEC) is logged and retried the next cycle; the loop never dies.
   systemd restarts the process on crash and via `WatchdogSec` on hang.

## Build & test

```bash
cd src
dotnet test                                   # 27 unit tests
dotnet publish -c Release -r linux-arm64 --self-contained \
  -p:PublishTrimmed=true -p:PublishSingleFile=true -o out   # Pi binary (~19 MB, single file)
```

Optional Native AOT (`-p:PublishAot=true`) needs a linux-arm64 cross toolchain or an ARM64 build
host; the code is fully AOT-compatible (analyzers enforced in the csproj).

## CI/CD

`.github/workflows/ci-cd.yml` runs on every push/PR to `main`: restore, `dotnet format`
check, build, tests, vulnerable-package check. Pushing a tag `v*` additionally publishes
the trimmed linux-arm64 single-file binary and creates a GitHub release with
`bwbAlarmWatcher2-linux-arm64.tar.gz` (binary, `appsettings.json`, systemd unit,
env example, `update.sh`).

Cut a release:

```bash
git tag v2.0.0 && git push origin v2.0.0
```

## Deploy (Raspberry Pi 5, Raspberry Pi OS Bookworm)

### First install

```bash
sudo apt install cec-utils jq curl
# private repo only: fine-grained PAT with contents:read on this repo
echo '<PAT>' | sudo tee /opt/BwbAlarmWatcher2/.github_token >/dev/null
sudo install -d /opt/BwbAlarmWatcher2 && sudo chmod 600 /opt/BwbAlarmWatcher2/.github_token
# fetch the updater once, then let it bootstrap everything (user, unit, env, binary)
curl -fsSL -H "Authorization: Bearer $(sudo cat /opt/BwbAlarmWatcher2/.github_token)" \
  https://raw.githubusercontent.com/Luca206/BwbAlarmWatcher2/main/deploy/update.sh \
  | sudo tee /opt/BwbAlarmWatcher2/update.sh >/dev/null
sudo chmod +x /opt/BwbAlarmWatcher2/update.sh
sudo /opt/BwbAlarmWatcher2/update.sh
# then set the token and TV IP:
sudoedit /opt/BwbAlarmWatcher2/bwbAlarmWatcher2.env
sudo systemctl restart bwbAlarmWatcher2.service
```

### Update to the latest release

```bash
sudo /opt/BwbAlarmWatcher2/update.sh          # no-op when already up to date
sudo /opt/BwbAlarmWatcher2/update.sh --force  # reinstall current version
```

### Configuration on the Pi

Settings are layered; later sources override earlier ones:

1. `/opt/BwbAlarmWatcher2/appsettings.json` — full settings, freely editable.
   `update.sh` never overwrites it (it is only created on first install).
2. `/opt/BwbAlarmWatcher2/bwbAlarmWatcher2.env` — environment variables
   (`Section__Key=value`), override `appsettings.json`. Keep secrets here
   (`Api__AuthToken`), mode 600. Also never touched by updates.

After any change: `sudo systemctl restart bwbAlarmWatcher2.service`, check with
`journalctl -u bwbAlarmWatcher2.service -f`.

## Open points before production

Tracked in `knowledge/20 Offene Punkte und Fragen.md`. The ones that affect this service:

- `subkind` semantics (`CLOSED` vs. `OPEN`) — verify against the live API, then adjust
  `Api__ClosedSubkinds` if needed (no code change required). If the GraphQL schema does not
  expose `subkind` on `Alarm` at all, remove it from `BuildAlarmsQuery` — alarms are then
  active purely by the `createdAfter` window, exactly like v1.
- WebSocket push protocol — not yet available; the service polls GraphQL until the operator
  provides the subscribe protocol.
- Static IP for the TV and ICMP answer behaviour of the LG — verify on site.
