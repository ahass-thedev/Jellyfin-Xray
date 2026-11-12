# The X-Ray sharing service. Build from the repository root:
#   docker build -f server/Dockerfile -t xray-share .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Jellyfin.Plugin.SceneXray.Sharing/ src/Jellyfin.Plugin.SceneXray.Sharing/
COPY server/Xray.Share.Server/ server/Xray.Share.Server/
RUN dotnet publish server/Xray.Share.Server -c Release -o /app --nologo

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# Non-root (the image's "app" user) and fit for a read-only root filesystem.
USER 1654
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080
ENTRYPOINT ["/app/xray-share"]
