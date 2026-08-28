# NOVA Deployment

Installs the ECS stack onto a [Wandelbots NOVA](https://docs.wandelbots.io) instance.

NOVA gives no direct Kubernetes access, so the installer drives the NOVA app API
(`/api/v2/cells/{cell}/apps`) instead. Each part of the stack becomes a NOVA app,
served at `http://<instance>/<cell>/<app-name>`.

NATS is not installed — NOVA runs its own broker and injects its address into
every app container as `NATS_BROKER`, which the coordinator and the client SDK
fall back to when `NATS_URL` is unset.

## What gets installed

| App | Image | Port | Health |
|---|---|---|---|
| `ecs-engine` | `engine` | 8080 | `/health` |
| `ecs-editor-api` | `editor-backend` | 5000 | `/health` |
| `ecs-editor` | `editor-frontend` | 80 | `/<cell>/ecs-editor/config.js` |
| `ecs-<system>` | one per `ECS_SYSTEM_IMAGES` entry | 8080 | `/health` |

The coordinator is installed first, before anything that talks to it.

NOVA restarts any app whose health probe fails. The coordinator and system
containers are headless, so they serve `/health` and `/app_icon.png` from
`HealthEndpoint`, which only listens when `HEALTH_PORT` is set — local and
Docker Compose runs are unaffected.

## Configuration

All configuration is via environment variables.

| Variable | Default | Purpose |
|---|---|---|
| `NOVA_BASE_URL` | `http://localhost:80` | NOVA instance root |
| `NOVA_ACCESS_TOKEN` | *(empty)* | Bearer token |
| `NOVA_CELL` | `cell` | Target cell |
| `ECS_APP_PREFIX` | `ecs` | Prefix for every app name |
| `ECS_IMAGE_REGISTRY` | `ghcr.io/ardo314/ecs-engine` | Registry the default images come from |
| `ECS_IMAGE_TAG` | `latest` | Tag for the default images |
| `ECS_ENGINE_IMAGE` | derived | Overrides the engine image |
| `ECS_EDITOR_BACKEND_IMAGE` | derived | Overrides the editor backend image |
| `ECS_EDITOR_FRONTEND_IMAGE` | derived | Overrides the editor frontend image |
| `ECS_SYSTEM_IMAGES` | *(empty)* | Comma-separated `name=image` or bare image references |
| `ECS_INSTALL_EDITOR` | `true` | Set `false` to skip both editor apps |
| `ECS_NATS_URL` | *(unset)* | Overrides the broker; unset means NOVA's `NATS_BROKER` is used |
| `ECS_TICK_RATE` | `20` | Coordinator tick rate |
| `ECS_REGISTRY_USER` | *(unset)* | Pull credentials; both parts required |
| `ECS_REGISTRY_PASSWORD` | *(unset)* | Pull credentials; both parts required |
| `ECS_DRY_RUN` | `false` | Print manifests instead of calling NOVA |

`ECS_SYSTEM_IMAGES` entries may be a bare image (`ghcr.io/acme/movement-system:1.0`,
whose repository segment becomes the app name) or an explicit `name=image` pair.
Names are reduced to RFC 1035 labels, as NOVA requires.

Exit codes: `0` success, `1` API or network failure, `2` bad configuration.

## Usage

Preview the manifests without touching the instance:

```bash
ECS_DRY_RUN=true dotnet run --project NovaInstaller
```

Install:

```bash
docker run --rm \
  -e NOVA_BASE_URL=https://your-instance \
  -e NOVA_ACCESS_TOKEN=$NOVA_TOKEN \
  -e NOVA_CELL=cell \
  -e ECS_SYSTEM_IMAGES=ghcr.io/ardo314/ecs-engine/nova-systems:latest \
  ghcr.io/ardo314/ecs-engine/nova-installer:latest
```

Re-running is an upgrade: any app whose name already exists is deleted, waited
out, and recreated from the new manifest. There is no uninstall mode — remove
apps from the NOVA UI or via the app API.

## Building

```bash
dotnet test deployments/nova/NovaInstaller.sln
docker build -t nova-installer deployments/nova
```
