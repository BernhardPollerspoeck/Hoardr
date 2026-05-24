# Hoardr

A self-hosted, OCI-compatible container registry that's actually pleasant to run.
`docker push` / `pull` just work — single process, no external database, cleans up after
itself, with a genuinely nice admin UI. Free and open source (MIT).

## Why Hoardr

I wanted my own container registry and didn't love the options:

- The **official Docker Registry** is bare-bones. Securing it means wrangling `htpasswd`
  files and a reverse proxy, there's no real user management, and no UI to speak of.
- **Harbor** and friends are powerful but heavy — multiple services, a database to operate,
  and a lot of moving parts for a single team or a homelab.

Nothing sat in the sweet spot: real **per-repository** access control, automatic **tag
retention + garbage collection** so the disk never silently fills up, a **clean UI** you can
actually look at, and a **CI-friendly** workflow — all in a single binary you can stand up in
minutes. So I built it.

Hoardr is opinionated and deliberately lean: serious features without the enterprise weight.
It runs as one .NET process, stores blobs on disk and everything else in an embedded
database — back up one folder and you've backed up the whole registry.

> [!NOTE]
> **Young project, but real.** Hoardr is new and the surface may still change. That said,
> I run it myself in production for my own images — it's built to actually work, not just to
> demo. Issues and PRs are very welcome.

## Features

- 🐳 **Works with Docker today** — full OCI Distribution spec, multi-arch (manifest lists)
- 📦 **Dead-simple to host** — one process, blobs on disk + embedded DB, no Postgres/Redis/sidecars
- 🔐 **Real access control** — per-repo **pull / push / delete**, master token + accounts
- 🤖 **Built for CI** — push-only tokens, `can_create` first-push, and **auto-`latest`** per repo
- 🧹 **Cleans up after itself** — hybrid tag retention + two-stage garbage collection
- 🔔 **Disk-space alerts** via [ntfy](https://ntfy.sh) before you run out of space
- 🗂️ **Backup is one folder** — blobs and all metadata live in a single directory
- 🌿 **Yours, forever** — MIT-licensed, self-hosted, no seats or usage limits

## Quickstart

```yaml
# docker-compose.yml
services:
  hoardr:
    image: ghcr.io/bernhardpollerspoeck/hoardr:latest
    container_name: hoardr
    restart: unless-stopped
    ports:
      - "8080:8080"
    environment:
      Hoardr__MasterToken: "change-me"   # use a long random secret
      Hoardr__DataRoot: "/data"
    volumes:
      - ./data:/data                      # blobs + database — back this up
```

```bash
docker compose up -d
docker login localhost:8080 -u master -p change-me
docker tag myapp localhost:8080/team/myapp:1.0
docker push localhost:8080/team/myapp:1.0
```

Then open the web UI, sign in with `master` + your token, and create accounts and
permissions under **Admin**. Port, built-in HTTPS, retention and ntfy alerts are all
configurable — see the **Setup** page in the running app, or [DEV.md](DEV.md).

## Configuration (essentials)

| Variable | Default | Purpose |
|---|---|---|
| `Hoardr__MasterToken` | — | admin token, full access (required) |
| `Hoardr__DataRoot` | `/data` | blobs + embedded database |
| `Hoardr__Http__Port` | `8080` | HTTP port |
| `Hoardr__Https__Enabled` | `false` | built-in HTTPS (cert via PFX or `.cer` + `.key`) |
| `Hoardr__Retention__KeepMin` | `10` | newest tags always kept per repo |
| `Hoardr__Retention__MaxAgeDays` | `30` | older tags beyond KeepMin are removed (0 = never) |

## Development

See **[DEV.md](DEV.md)** — build, run, local `docker login`, tests, and config reference.

## License

[MIT](LICENSE) · built by [Bernhard Pollerspöck](https://github.com/BernhardPollerspoeck)
for everyone.
