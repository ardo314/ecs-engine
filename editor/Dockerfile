FROM node:22-alpine AS build
WORKDIR /app
COPY package.json package-lock.json ./
COPY proto/gen/ts/package.json proto/gen/ts/
COPY clients/typescript/package.json clients/typescript/
COPY editor/package.json editor/
COPY examples/typescript/movement-system/package.json examples/typescript/movement-system/
COPY deployments/nova/package.json deployments/nova/
RUN npm ci
COPY proto/gen/ts/ proto/gen/ts/
COPY clients/typescript/ clients/typescript/
COPY editor/ editor/
# Only the editor's own chain: the root build script also covers packages this image has no source for.
RUN npm run build --workspace @ecs/protos \
 && npm run build --workspace @ecs/client \
 && npm run build --workspace @ecs/editor

FROM node:22-alpine
WORKDIR /app
ENV NODE_ENV=production
COPY package.json package-lock.json ./
COPY proto/gen/ts/package.json proto/gen/ts/
COPY clients/typescript/package.json clients/typescript/
COPY editor/package.json editor/
COPY examples/typescript/movement-system/package.json examples/typescript/movement-system/
COPY deployments/nova/package.json deployments/nova/
RUN npm ci --omit=dev --workspace @ecs/editor
COPY --from=build /app/proto/gen/ts/dist proto/gen/ts/dist
COPY --from=build /app/clients/typescript/dist clients/typescript/dist
COPY --from=build /app/editor/dist editor/dist
WORKDIR /app/editor
EXPOSE 8080
CMD ["node", "dist/server/index.js"]
