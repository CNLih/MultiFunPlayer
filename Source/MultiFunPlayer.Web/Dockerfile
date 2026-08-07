# Frontend-only preview image. It does not require the MultiFunPlayer Server.
FROM node:22-alpine AS build
WORKDIR /web
COPY package.json tsconfig.json vite.config.ts ./
COPY public ./public
COPY src ./src
COPY index.html ./index.html
RUN npm ci --no-audit --no-fund && npm run build

FROM nginx:1.27-alpine
COPY nginx.conf /etc/nginx/nginx.conf
COPY --from=build /web/dist /usr/share/nginx/html
RUN mkdir -p /config /keys /scripts /logs \
  && chown -R nginx:nginx /usr/share/nginx/html /config /keys /scripts /logs
USER nginx
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=3s --start-period=5s CMD wget -q -O /dev/null http://127.0.0.1:8080/ || exit 1
