FROM oven/bun:1.4.0 AS build
WORKDIR /app
COPY package.json bun.lock ./
RUN bun install --frozen-lockfile
COPY dashboard ./dashboard
RUN bun run check && bun run build

FROM oven/bun:1.4.0 AS runtime
WORKDIR /app
ENV NODE_ENV=production HOST=0.0.0.0 PORT=8080 TELEMETRY_PATH=/telemetry/snapshot.json REPORTS_DIRECTORY=/reports
USER root
RUN mkdir /reports && chown bun:bun /reports
COPY --from=build --chown=bun:bun /app/dashboard/dist ./dashboard/dist
COPY --chown=bun:bun dashboard/server ./dashboard/server
COPY --chown=bun:bun dashboard/shared ./dashboard/shared
USER bun
EXPOSE 8080/tcp
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s CMD bun -e 'const r = await fetch("http://127.0.0.1:" + (process.env.PORT || "8080") + "/healthz"); process.exit(r.ok ? 0 : 1)'
CMD ["bun", "dashboard/server/index.ts"]
