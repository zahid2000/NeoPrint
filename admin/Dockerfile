# syntax=docker/dockerfile:1

# ---------------------------------------------------------------------------
# Build the panel
# ---------------------------------------------------------------------------
FROM node:22.22.3-alpine AS build

WORKDIR /src

# The lockfile alone first, so a source change does not reinstall everything.
COPY package.json package-lock.json ./
RUN npm ci

COPY . .

# `npm run build` defaults to the production configuration (angular.json).
RUN npm run build

# ---------------------------------------------------------------------------
# Serve it
# ---------------------------------------------------------------------------
FROM nginx:1.29.3-alpine AS runtime

# nginx proxies /api and /hubs to the backend, so the panel and the API share
# one origin: the admin refresh cookie is SameSite=Strict and would never be
# sent across origins, and no CORS configuration is needed (plan section 8A.1).
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY security-headers.conf /etc/nginx/snippets/security-headers.conf
# The page's Content-Security-Policy names the map's host (T-217): filled in
# from the environment when the container starts (the image's own envsubst
# step), so an own tile server is a setting of the container, not a new image.
COPY content-security-policy.conf.template /etc/nginx/templates/content-security-policy.conf.template
ENV NEOPRINT_MAP_ORIGIN=https://tiles.openfreemap.org
ENV NGINX_ENVSUBST_OUTPUT_DIR=/etc/nginx/snippets
COPY --from=build /src/dist/admin/browser /usr/share/nginx/html

# The stock image already runs the workers as nginx; this drops the root
# master process as well, which needs the pid and cache paths to be writable
# by that user.
RUN touch /var/run/nginx.pid \
 && chown -R nginx:nginx /var/run/nginx.pid /var/cache/nginx /usr/share/nginx/html /etc/nginx/snippets

USER nginx

EXPOSE 80

# 127.0.0.1, not localhost: see the healthcheck in docker-compose.yml (T-274).
HEALTHCHECK --interval=15s --timeout=5s --start-period=10s --retries=5 \
  CMD wget -q -O /dev/null http://127.0.0.1:80/healthz || exit 1
