# The operations UI: a static bundle behind nginx, deployed separately from the service it
# talks to. nginx serves the SPA and proxies /api to the service, so in the Compose stack
# the browser sees one origin and the service needs no CORS.
FROM node:22-alpine AS build
WORKDIR /ui
COPY package.json package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY . .
RUN npm run build

FROM nginx:1.27-alpine
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /ui/dist /usr/share/nginx/html
EXPOSE 8083
HEALTHCHECK --interval=15s --timeout=3s --retries=3 CMD wget -q -O /dev/null http://127.0.0.1:8083/ || exit 1
