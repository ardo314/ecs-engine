FROM node:22-alpine AS build
WORKDIR /app
COPY package.json ./
RUN npm install
COPY . .
RUN npm run build

FROM nginx:alpine
COPY --from=build /app/dist /opt/site
COPY nginx.conf.template /opt/nginx.conf.template
COPY docker/25-editor-config.sh /docker-entrypoint.d/25-editor-config.sh
RUN sed -i 's/\r$//' /docker-entrypoint.d/25-editor-config.sh \
    && chmod +x /docker-entrypoint.d/25-editor-config.sh
ENV BASE_PATH=/ \
    EDITOR_BACKEND_URL=http://localhost:5000
EXPOSE 80
