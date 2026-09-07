FROM node:22-alpine AS build
WORKDIR /app
COPY package.json package-lock.json ./
COPY proto/gen/ts/package.json proto/gen/ts/
COPY clients/typescript/package.json clients/typescript/
COPY editor/package.json editor/
COPY examples/typescript/movement-system/package.json examples/typescript/movement-system/
COPY deployments/nova/package.json deployments/nova/
RUN npm ci --workspace @ecs/nova-installer
COPY deployments/nova/ deployments/nova/
RUN npm run build --workspace @ecs/nova-installer

FROM node:22-alpine
WORKDIR /app
ENV NODE_ENV=production
# Baked in so the installer deploys the images built from its own revision.
ARG ECS_IMAGE_TAG=latest
ENV ECS_DEFAULT_IMAGE_TAG=$ECS_IMAGE_TAG
COPY package.json package-lock.json ./
COPY proto/gen/ts/package.json proto/gen/ts/
COPY clients/typescript/package.json clients/typescript/
COPY editor/package.json editor/
COPY examples/typescript/movement-system/package.json examples/typescript/movement-system/
COPY deployments/nova/package.json deployments/nova/
RUN npm ci --omit=dev --workspace @ecs/nova-installer
COPY --from=build /app/deployments/nova/dist deployments/nova/dist
WORKDIR /app/deployments/nova
EXPOSE 8080
CMD ["node", "dist/main.js"]
