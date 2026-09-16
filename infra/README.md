# Infrastructure

## Implemented

The root `compose.yaml` runs PostgreSQL, RabbitMQ, a storage initializer, migrations,
the API, the worker and the nginx frontend for local development and validation.
Images have health checks, explicit resource limits and a vulnerability gate.

`terraform/environments/{dev,staging,prod}` composes Azure networking, private data
services, managed identities, Key Vault, Container Apps, Front Door and observability.
The environments require explicit inputs; they are definitions, not evidence of
running cloud infrastructure. API/worker media uses private Azure Blob storage.
Cloud RabbitMQ must be provisioned separately with a durable TLS endpoint.

## Structural validation

Use Terraform 1.16.3. From the repository root:

```sh
terraform fmt -check -recursive infra/terraform
for environment in dev staging prod; do
  terraform -chdir=infra/terraform/environments/$environment init -backend=false -input=false -lockfile=readonly
  terraform -chdir=infra/terraform/environments/$environment validate
done
```

Initialization downloads the exact locked providers. Validation checks module
structure and provider schemas; it does not create resources or prove Azure
permissions, quotas, connectivity, managed identity or recovery behavior.
CI also scans the downloaded provider binaries and fails on HIGH/CRITICAL findings.

Before any future deployment, configure protected remote state, supply reviewed
environment inputs and inspect a plan. Follow the runtime/migration-role separation,
provider registration, private-network and key-management requirements in
[operations](../docs/OPERATIONS.md#azure). No workflow runs `terraform apply`.

## Future work

Kubernetes manifests, Kustomize overlays and Argo CD applications are not part of
this release. A future Kubernetes implementation needs explicit choices for
secrets, private media storage, migration ordering, probes, resource limits,
network policy and durable PostgreSQL/RabbitMQ services. It must add manifest and
Argo validation before claiming deployment support. No cluster has been deployed.
