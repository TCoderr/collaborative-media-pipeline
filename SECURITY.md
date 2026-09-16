# Security policy

## Reporting a vulnerability

Report suspected vulnerabilities privately to the maintainer at
`tsinanergun@gmail.com`. Include the affected commit, a minimal reproduction,
expected impact and relevant redacted logs. Do not publish credentials, private
media or exploitable details in a public issue. Response times are not guaranteed.

## Supported scope

Security fixes target the current `main` branch. This repository provides a local
application and infrastructure definitions; it does not claim an operated or
independently certified production service.

## Controls and verification

- Uploaded sources remain private and quarantined until signature, type, size,
  checksum and bounded image decoding checks succeed. This is not a malware scan.
- Project membership and role checks protect versions, reviews and content.
  PostgreSQL constraints, optimistic concurrency, outbox and inbox records protect
  lifecycle consistency and duplicate handling.
- CI rejects HIGH or CRITICAL vulnerabilities in final runtime images, build
  images, supporting tools and Terraform provider binaries. Scanner errors fail
  validation; findings are not suppressed.
- npm and transitive NuGet audits and a secret scan of publication files run in
  CI. Local secrets, uploads, database files, generated outputs and Terraform state
  are excluded from version control.

A clean scan describes the dependencies and vulnerability database at scan time.
Keep dependencies and base images updated and rerun the full gate before release.
Supporting-tool source pins, patches and verification instructions are documented
in [operations](docs/OPERATIONS.md#supporting-tool-maintenance).

## Before operating an environment

Follow the [operations guidance](docs/OPERATIONS.md) for separate runtime and
migration database roles, TLS, persistent encryption keys, private storage,
bounded workers and cloud prerequisites. Choose retention, backups, restore
testing, monitoring and incident response policies for the intended environment.
No workflow in this repository deploys infrastructure or application images.
