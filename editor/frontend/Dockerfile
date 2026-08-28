FROM node:22-alpine AS build
WORKDIR /app
COPY package.json ./
RUN npm install
COPY . .

# Vite bakes the base path into asset URLs, so BASE_PATH is fixed at build time.
ARG BASE_PATH=/
ARG EDITOR_BACKEND_URL=http://localhost:5000
ENV VITE_EDITOR_BACKEND_URL=${EDITOR_BACKEND_URL}
RUN set -eu; \
    BASE_PATH=$(printf '/%s/' "$BASE_PATH" | sed 's|//*|/|g'); \
    export BASE_PATH; \
    npm run build; \
    mkdir -p "/site${BASE_PATH}"; \
    cp -a dist/. "/site${BASE_PATH}"; \
    sed "s|__BASE_PATH__|${BASE_PATH}|g" nginx.conf.template > /default.conf

FROM nginx:alpine
COPY --from=build /site /usr/share/nginx/html
COPY --from=build /default.conf /etc/nginx/conf.d/default.conf
EXPOSE 80
