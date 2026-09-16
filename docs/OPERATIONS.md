# Operations and security

## Runtime

Use Docker Compose for development and isolated verification. Local `.env` files and all media, keys, databases, logs and test outputs stay outside Git. Compose binds published ports to loopback. Runtime API and worker images run as UID 1654 with read-only root filesystems, dropped capabilities, private media volumes and bounded memory. PostgreSQL and RabbitMQ use durable volumes. Rebuild routinely with current base packages; tags alone do not guarantee a clean vulnerability result.

When Compose recreates an API container while retaining the frontend container,
restart the frontend with `docker compose restart frontend` to refresh nginx's
upstream DNS resolution. Then run `node scripts/check-container-health.mjs`; it
checks API liveness and readiness through the frontend as well as container state.

The worker supports inspection/reconciliation commands implemented in `OperationsCommands.cs`. Inspection is read-only. Review abandoned outbox rows, stale reservations, exhausted jobs and dead-letter messages before retrying anything. Never delete inbox entries to force a retry. Use the authorised reprocess API for transient terminal jobs. Keep quarantined media private; retention and deletion require an explicit operational policy.

Health liveness checks the process. Readiness checks required storage/database state and, for workers, an attached broker consumer. API readiness includes the expected migration state. Broker availability is additionally reported as an API diagnostic; accepted uploads can remain in a durable outbox during a broker outage.

## Azure

Terraform defines private PostgreSQL Flexible Server, Blob Storage, Key Vault, managed identities, Container Apps, a manual migration job, observability and Front Door. The frontend proxies same-origin API and websocket traffic to internal API ingress. Front Door caching is disabled for the route. Supply current reviewed AzureFrontDoor.Backend CIDRs to restrict frontend ingress; the API also checks the profile identifier. Never treat the header alone as a network boundary.

Cookie keys are shared in a private Blob container and wrapped with a versionless Key Vault key. Preserve previous Key Vault key versions while cookies encrypted by them remain valid. Development can instead persist a local key directory. Production refuses to start without a configured persistent key store.

The runtime database connection must reference a separately provisioned DML-only role. The generated administrator secret is reserved for the one-shot migration job. Grant runtime SELECT/INSERT/UPDATE/DELETE on application tables as appropriate, INSERT/SELECT only on `state_history`, and no DDL or ownership privileges. Configure default privileges for future migrations. The DB triggers additionally protect source metadata and history. Provide the runtime secret URI through `runtime_database_secret_id`; do not substitute the migration administrator.

AzureRM 5.5 uses explicit subscription prerequisites: register Microsoft.App, Microsoft.Cdn, Microsoft.ContainerRegistry, Microsoft.DBforPostgreSQL, Microsoft.Insights, Microsoft.KeyVault, Microsoft.ManagedIdentity, Microsoft.Network, Microsoft.OperationalInsights and Microsoft.Storage before a future plan/apply. Automatic provider registration is disabled. Storage data-plane access uses Azure AD, consistent with disabled Shared Key authorization.

Terraform requires a pre-provisioned TLS RabbitMQ endpoint and Key Vault secret references; the cloud module does not pretend Container Apps ephemeral storage is a durable broker. Compose supplies the local broker. A private-network-capable runner with data-plane permissions is needed for Blob/Key Vault provisioning. Terraform state contains generated secrets and must use encrypted, access-controlled remote storage. Supply cloud inputs and inspect a plan before any future deployment. No workflow runs apply or deploys containers.

## Verification and gates

Run `terraform init -backend=false` and `terraform validate` in each environment for offline structural checks. This is not an Azure deployment test. CI runs npm and NuGet audits and an unsuppressed HIGH/CRITICAL image gate covering the API, worker, frontend, PostgreSQL, RabbitMQ, storage initializer and build tools. Scanner failures fail the job. There are no vulnerability ignore files and no ignore-unfixed exception.

Read security output as an observation at scan time, not a permanent guarantee. Rebuild and rescan final images after dependency, source or base-image changes. Protect the main branch by requiring both application and Terraform checks; repository branch protection is an external setting.

The archive frontend intentionally does not add new authentication or authoring screens. Use authenticated API clients for those actions. Image processing supports still images only. Delivery is an auditable confirmation, not an external delivery integration. OTLP export requires a configured collector; cloud connectivity, managed identity, private networking and backup restoration must be verified in a future authorised environment.

## Supporting-tool maintenance

`bash scripts/install-supporting-tools.sh` builds Linux tools from checksum-pinned upstream release source archives using a digest-pinned Go 1.26.8 toolchain. The Linux amd64 output SHA-256 hashes are pinned in `infra/tools/supporting-tools.sha256` and verified before execution or plugin installation. The local versions are explicitly marked `0.74.0-quire.1` (Trivy) and `v0.37.1-quire.2` (Buildx); these are project rebuilds, not official patched releases. Trivy retains its upstream JSON build option and updates gRPC to 1.83.2. Buildx updates go-archive to 0.3.0 and replaces its retired Docker module dependency with its existing UUID library for automatic builder names. Generated names now use `builder_<UUID>`; explicit names and image-building semantics are preserved. Focused upstream tests run before binaries are produced. No application source is patched.

The Buildx patch removes the retired `github.com/docker/docker` module from compiled dependencies. The build checks that no import from that module remains. Store tests cover generated-name persistence, collisions and entropy failure. No ignore, VEX exemption, package renaming or dependency-metadata stripping is used. The security gate scans the toolchain and rebuilt supporting-tool images as well as the actual installed binaries.

When replacing these rebuilds with upstream releases, verify upstream provenance/checksums, scan the actual final binaries, retain the same gate thresholds, and rerun container builds and application regression tests.

The installer also verifies and installs the official Docker Compose 5.5.1 Linux
amd64 release against its pinned SHA-256. CI therefore audits and uses the same
Compose binary as local verification, independent of the runner's bundled plugin.
Run `python3 scripts/security-secrets.py` after installing supporting tools to
scan all tracked and publishable untracked files. The scanner receives a temporary
copy of that file set; ignored local state is never copied or published.
