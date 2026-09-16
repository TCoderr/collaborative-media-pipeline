FROM postgres:16-alpine
RUN apk upgrade --no-cache && apk add --no-cache su-exec \
    && sed -i 's/exec gosu /exec su-exec /g' /usr/local/bin/docker-entrypoint.sh \
    && rm /usr/local/bin/gosu
