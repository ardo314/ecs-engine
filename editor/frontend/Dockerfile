FROM node:22-alpine AS build
WORKDIR /app
COPY package.json ./
RUN npm install
COPY . .
ARG BASE_PATH=/
ARG EDITOR_BACKEND_URL=http://localhost:5000
ENV VITE_EDITOR_BACKEND_URL=${EDITOR_BACKEND_URL}
RUN BASE_PATH=$(printf '/%s/' "$BASE_PATH" | sed 's|//*|/|g') && \
    export BASE_PATH && npm run build

FROM nginx:alpine
ENV BASE_PATH=/
COPY --from=build /app/dist /app/dist
COPY nginx.conf.template /etc/nginx/templates/default.conf.template
COPY 40-base-path.envsh /docker-entrypoint.d/40-base-path.envsh
RUN chmod +x /docker-entrypoint.d/40-base-path.envsh
EXPOSE 80
