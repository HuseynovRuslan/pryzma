# MEYDAN web image — multi-stage: build the Vite SPA with Node, serve it (and proxy /api) with nginx.
# Build CONTEXT is the frontend/ directory (deploy/compose.yml sets it):
#   docker build -f frontend/Dockerfile -t meydan-web frontend
#
# Nothing environment-specific is baked in: the bundle talks to /api on its own origin.

# ---- build stage ----
FROM node:22-alpine AS build
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY . .
RUN npm run build

# ---- runtime stage ----
FROM nginx:1.27-alpine AS runtime
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY nginx-proxy.conf /etc/nginx/meydan-proxy.conf
COPY nginx-headers.conf /etc/nginx/meydan-headers.conf
COPY --from=build /app/dist /usr/share/nginx/html
# This build's hashed files, kept a second time outside the served directory: that directory is a volume shared
# with the releases before this one, and the entrypoint merges this copy into it rather than replacing it, so a
# tab still running the previous bundle can finish loading its chunks. See nginx-assets.sh.
RUN mkdir -p /usr/share/nginx/assets-build \
    && cp -R /usr/share/nginx/html/assets/. /usr/share/nginx/assets-build/
COPY nginx-assets.sh /docker-entrypoint.d/40-meydan-assets.sh
RUN chmod +x /docker-entrypoint.d/40-meydan-assets.sh
EXPOSE 80
HEALTHCHECK --interval=15s --timeout=3s --start-period=5s --retries=3 \
    CMD wget -qO /dev/null http://127.0.0.1/ || exit 1
CMD ["nginx", "-g", "daemon off;"]
