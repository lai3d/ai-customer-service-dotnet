# The operations UI: a static bundle behind nginx, deployed separately from the service it
# talks to. nginx serves the SPA and proxies /api to the service, so the browser sees one
# origin and the service needs no CORS. The unprivileged image runs as uid 101 and keeps
# its writable paths under /tmp, so the pod can have a read-only root filesystem.
FROM node:22-alpine AS build
WORKDIR /ui
COPY package.json package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY . .
RUN npm run build

FROM nginxinc/nginx-unprivileged:1.27-alpine
# The image's entrypoint renders /etc/nginx/templates/*.template with envsubst into
# /etc/nginx/conf.d at start, so one built image serves any environment.
COPY templates/default.conf.template /etc/nginx/templates/default.conf.template
COPY --from=build /ui/dist /usr/share/nginx/html
ENV ADMIN_API_UPSTREAM=app:8082
EXPOSE 8083
HEALTHCHECK --interval=15s --timeout=3s --retries=3 CMD wget -q -O /dev/null http://127.0.0.1:8083/ || exit 1
