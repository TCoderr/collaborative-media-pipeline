# QUIRE — Collaborative Media Pipeline

QUIRE is a collaborative media pipeline for immutable versions, safe processing, live review workflows and observable event-driven delivery. The repository's technical name is `collaborative-media-pipeline`.

The Three.js spiral, GLSL shaders, GSAP and Lenis archive displays authorised backend versions. Signed-out, unavailable, processing and quarantined states are explicit; there is no demo fallback.

## Capabilities

- ASP.NET Core and EF Core model projects, assets, immutable source versions,
  review threads, approvals, delivery confirmations and append-only history.
  PostgreSQL is the source of truth, with database constraints and optimistic concurrency.
- Uploads enter private quarantine. Content signatures, declared types, byte limits,
  checksums and decoded dimensions are checked before previews become available.
- Bounded RabbitMQ workers create SkiaSharp WebP thumbnails and previews from
  supported still images. Durable jobs record attempts, retries, terminal failures
  and dead-letter handling; failed processing never becomes ready.
- Transactional outbox and idempotent inbox handling keep database changes and
  events consistent across retries and duplicate delivery.
- SignalR events refresh the HTTP archive projection. Review ownership,
  independent approval and owner-confirmed delivery are enforced by the API.
- OpenTelemetry traces HTTP, PostgreSQL, outbox, RabbitMQ, processing and live
  updates, with metrics for actual work and lifecycle activity.
- Docker Compose supplies the local stack. Azure-oriented Terraform defines
  Container Apps, private PostgreSQL and Blob storage, Key Vault and observability.
  Cloud deployment and operational recovery remain environment-specific validation work.

The lifecycle is **upload → quarantine/validate → process → ready → review →
approve → deliver**. Delivery records a confirmed handoff; it does not transfer
files to an external delivery service.

## Local stack

Requirements: Docker Engine with Compose, Node 24 for frontend development, and .NET SDK 9.0.318 for backend development. `global.json` pins the SDK used by local builds, containers and CI.

1. Copy `.env.example` to `.env` and set unique local PostgreSQL and RabbitMQ passwords.
2. Run `docker compose up --build -d --wait --wait-timeout 180`.
3. Open `http://localhost:5195`. API: `http://localhost:5295`.
4. Register or sign in through `/api/v1/auth/register` or `/api/v1/auth/login`, using the frontend origin so its session cookie accompanies archive requests. Authentication and authoring currently use the API; the frontend is an archive viewer.

Migrations run as a one-shot container before the API and worker start. Local media, keys and database state use named volumes. `docker compose down` stops the stack and retains data. Ports can be overridden with `POSTGRES_PORT` and `RABBITMQ_PORT`.

For frontend development, run `npm ci` and `npm run dev`; Vite proxies API and hub requests to port 5295. `npm run build` creates the static production bundle. Use the nginx container when serving that bundle with the backend.

## Validation

```sh
npm test
npm run build
dotnet test backend/MediaPipeline.sln
npm run test:e2e
node scripts/check-container-health.mjs
```

Integration tests require a running PostgreSQL and RabbitMQ. Set `MEDIA_PIPELINE_TEST_POSTGRES` to an isolated test administrator connection string, and `MEDIA_PIPELINE_TEST_RABBITMQ_HOST`, `_PORT`, `_VHOST`, `_USER`, `_PASSWORD` to an isolated broker virtual host. Tests create and remove their own database; run transport suites sequentially within a virtual host. Stop the application worker while integration transport tests use that host. Browser tests require the real API and worker; set `QUIRE_WEB_URL` to test the container frontend.

CI builds all runtime images, runs database/broker integration tests, checks container health, exercises the real browser lifecycle, audits dependencies and publication files, and fails on any unsuppressed HIGH or CRITICAL vulnerability in runtime images, build images, supporting tools or provider binaries. Terraform validation covers dev, staging and production. CI does not publish images or deploy infrastructure.

## Infrastructure scope

The implemented deployment definitions target Azure Container Apps through
Terraform. Kubernetes manifests, Kustomize overlays and Argo CD applications are
future work; this release does not include or claim validation of those resources.
See the [infrastructure guide](infra/README.md) for current validation commands and
cloud prerequisites. No cloud environment has been deployed by these workflows.

- [Architecture and invariants](docs/ARCHITECTURE.md)
- [API and concurrency contracts](docs/API.md)
- [Operations and security](docs/OPERATIONS.md)
- [Preserved frontend mechanics](docs/FRONTEND.md)
- [Asset provenance](ASSETS.md)
- [Security policy](SECURITY.md)
