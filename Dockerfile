# syntax=docker/dockerfile:1

# ---- Stage 1: build the React/Vite/TypeScript web client ----
FROM node:22-alpine AS web-build
WORKDIR /src/web
COPY src/web/package.json src/web/package-lock.json ./
RUN npm ci
COPY src/web/ ./
RUN npm run build

# ---- Stage 2: restore and publish the ASP.NET Core backend ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend-build
WORKDIR /src

COPY Directory.Build.props ./
COPY MultiChannelAgent.sln ./
COPY src/MultiChannelAgent.Domain/MultiChannelAgent.Domain.csproj src/MultiChannelAgent.Domain/
COPY src/MultiChannelAgent.Application/MultiChannelAgent.Application.csproj src/MultiChannelAgent.Application/
COPY src/MultiChannelAgent.Infrastructure/MultiChannelAgent.Infrastructure.csproj src/MultiChannelAgent.Infrastructure/
COPY src/MultiChannelAgent.Host/MultiChannelAgent.Host.csproj src/MultiChannelAgent.Host/
RUN dotnet restore src/MultiChannelAgent.Host/MultiChannelAgent.Host.csproj

COPY src/ src/
RUN dotnet publish src/MultiChannelAgent.Host/MultiChannelAgent.Host.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

# ---- Stage 3: runtime image combining backend publish output and the built web client ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

RUN useradd --uid 5678 --user-group --no-create-home --shell /usr/sbin/nologin appuser
USER appuser

COPY --from=backend-build --chown=appuser:appuser /app/publish ./
COPY --from=web-build --chown=appuser:appuser /src/web/dist ./wwwroot

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "MultiChannelAgent.Host.dll"]
