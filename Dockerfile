FROM node:25-alpine AS build
RUN apk upgrade --no-cache && npm install --global npm@12.0.2
# Patch the build tool's own vulnerable dependencies until npm bundles these fixes.
RUN node -e "const fs=require('fs'),f='/usr/local/lib/node_modules/npm/package.json',p=JSON.parse(fs.readFileSync(f));delete p.devDependencies;delete p.workspaces;p.overrides={...p.overrides};for(const [n,v] of Object.entries({'tar':'7.5.21','ip-address':'10.3.1','brace-expansion':'5.0.9'})){p.overrides[n]=v;if(p.dependencies[n])p.dependencies[n]=v;}fs.writeFileSync(f,JSON.stringify(p));" \
    && npm install --prefix /usr/local/lib/node_modules/npm --ignore-scripts --omit=dev --package-lock=false
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci --ignore-scripts
COPY index.html vite.config.js ./
COPY src ./src
COPY public ./public
RUN npm run build

FROM nginxinc/nginx-unprivileged:stable-alpine
USER root
RUN apk upgrade --no-cache
USER 101
COPY --from=build /app/dist /usr/share/nginx/html
COPY infra/nginx/default.conf.template /etc/nginx/templates/default.conf.template
ENV API_UPSTREAM=http://api:8080 API_HOST=api
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=3s CMD wget -q -O /dev/null http://127.0.0.1:8080/health/live || exit 1
