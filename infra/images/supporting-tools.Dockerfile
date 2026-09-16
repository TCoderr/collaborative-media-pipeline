FROM golang:1.26.8-alpine3.23@sha256:33ce311e5eecedee48ec1b84419c1306e9fbd71009f0d5c3f2a6904b579c1ecc AS toolchain
RUN apk upgrade --no-cache && apk add --no-cache git
ENV CGO_ENABLED=0 GOFLAGS=-mod=mod GOTOOLCHAIN=local GOMEMLIMIT=2GiB

FROM toolchain AS trivy-build
ENV GOEXPERIMENT=jsonv2
WORKDIR /src
RUN wget -q -O source.tar.gz https://api.github.com/repos/aquasecurity/trivy/tarball/e1fd17a0ea4a8cf24bc4b4dd7e2cfbf4bb31b994 \
    && echo 'df71453efeb60ca8f9f34b04ee9999d1249eb8dfca66dfeddf3b6077595fc023  source.tar.gz' | sha256sum -c - \
    && tar -xzf source.tar.gz --strip-components=1 && rm source.tar.gz
RUN --mount=type=cache,target=/go/pkg/mod --mount=type=cache,target=/root/.cache/go-build \
    go get google.golang.org/grpc@v1.83.2 \
    && go test -p 2 ./pkg/rpc/... ./pkg/vulnerability/... \
    && go build -p 2 -trimpath -buildvcs=false -ldflags='-s -w -X github.com/aquasecurity/trivy/pkg/version/app.ver=0.74.0-quire.1' -o /out/trivy ./cmd/trivy

FROM toolchain AS buildx-build
WORKDIR /src
RUN wget -q -O source.tar.gz https://api.github.com/repos/docker/buildx/tarball/0b265a9f62db554fa9aba6dd19e1bd5704bc7d8a \
    && echo 'd9ceb07a9520b61a8c8ec7d903b4cd6579273344a0236c03dd8b36adbf206729  source.tar.gz' | sha256sum -c - \
    && tar -xzf source.tar.gz --strip-components=1 && rm source.tar.gz
COPY infra/tools/buildx-builder-names.patch /tmp/builder-names.patch
COPY infra/tools/buildx_builder_names_test.go /src/store/builder_names_test.go
RUN git apply /tmp/builder-names.patch && gofmt -w store/util.go store/builder_names_test.go
RUN --mount=type=cache,target=/go/pkg/mod --mount=type=cache,target=/root/.cache/go-build \
    go get github.com/moby/go-archive@v0.3.0 \
    && go mod edit -droprequire=github.com/docker/docker \
    && go mod tidy \
    && go list -deps ./cmd/buildx > /tmp/buildx-dependencies.txt \
    && ! grep -q '^github.com/docker/docker/' /tmp/buildx-dependencies.txt \
    && go test -p 2 ./store/... ./util/dockerutil/... \
    && go build -p 2 -trimpath -buildvcs=false -ldflags='-s -w -X github.com/docker/buildx/version.Version=v0.37.1-quire.2 -X github.com/docker/buildx/version.Revision=0b265a9f62db554fa9aba6dd19e1bd5704bc7d8a -X github.com/docker/buildx/version.Package=github.com/docker/buildx' -o /out/docker-buildx ./cmd/buildx

FROM scratch AS trivy-tools
COPY --from=trivy-build /out/trivy /trivy
COPY --from=trivy-build /src/LICENSE /licenses/trivy/LICENSE

FROM scratch AS buildx-tools
COPY --from=buildx-build /out/docker-buildx /docker-buildx
COPY --from=buildx-build /src/LICENSE /licenses/buildx/LICENSE

FROM trivy-tools AS tools
COPY --from=buildx-tools /docker-buildx /docker-buildx
COPY --from=buildx-tools /licenses/buildx/LICENSE /licenses/buildx/LICENSE
