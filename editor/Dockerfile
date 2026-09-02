FROM node:22-alpine AS build
WORKDIR /app
COPY package.json package-lock.json ./
COPY proto/gen/ts/package.json proto/gen/ts/
COPY editor/package.json editor/
RUN npm ci
COPY proto/gen/ts/ proto/gen/ts/
COPY editor/ editor/
RUN npm run build

FROM node:22-alpine
WORKDIR /app
ENV NODE_ENV=production
COPY package.json package-lock.json ./
COPY proto/gen/ts/package.json proto/gen/ts/
COPY editor/package.json editor/
RUN npm ci --omit=dev
COPY --from=build /app/proto/gen/ts/dist proto/gen/ts/dist
COPY --from=build /app/editor/dist editor/dist
WORKDIR /app/editor
EXPOSE 8080
CMD ["node", "dist/server/index.js"]
