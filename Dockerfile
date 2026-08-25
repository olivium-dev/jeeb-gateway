# ---------- build stage ----------
FROM mcr.microsoft.com/dotnet/sdk:8.0@sha256:306301580fcaa5b445180e759db59309979002d1000669cb4cf58a567d0014bc AS build
WORKDIR /src

COPY src/JeebGateway/JeebGateway.csproj src/JeebGateway/
RUN dotnet restore src/JeebGateway/JeebGateway.csproj

COPY . .
RUN dotnet publish src/JeebGateway/JeebGateway.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

# ---------- runtime stage ----------
FROM mcr.microsoft.com/dotnet/aspnet:8.0.29-alpine3.23@sha256:b02ab6637e02dfe07d4205d557cbce7e2ab0e4a1d7d1285868b4f31eed20bd10 AS runtime
WORKDIR /app

RUN addgroup -S -g 65532 appgroup \
    && adduser -S -D -H -u 65532 -G appgroup -s /sbin/nologin appuser
USER appuser

COPY --from=build /app/publish .

ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD wget --no-verbose --tries=1 --spider http://localhost:8080/health/live || exit 1

ENTRYPOINT ["dotnet", "JeebGateway.dll"]
