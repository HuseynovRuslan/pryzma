# MEYDAN API image — multi-stage. Build CONTEXT is the backend/ directory (deploy/compose.yml sets it):
#   docker build -f backend/Dockerfile -t meydan-api backend
#
# The same image runs the API and the explicit migration step (`dotnet Meydan.Api.dll migrate`).

# ---- build stage: full SDK ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Project files first, so `restore` stays cached until a .csproj changes.
COPY src/Meydan.Domain/Meydan.Domain.csproj                 src/Meydan.Domain/
COPY src/Meydan.Infrastructure/Meydan.Infrastructure.csproj src/Meydan.Infrastructure/
COPY src/Meydan.Api/Meydan.Api.csproj                       src/Meydan.Api/
RUN dotnet restore src/Meydan.Api/Meydan.Api.csproj -r linux-x64

COPY src/ src/
# Single MSBuild node, no compiler server: the shared VPS runs other people's production next to this build.
RUN DOTNET_gcServer=0 dotnet publish src/Meydan.Api/Meydan.Api.csproj \
    -c Release -r linux-x64 --self-contained false -o /app --no-restore \
    -p:UseSharedCompilation=false -maxcpucount:1

# ---- runtime stage: ASP.NET runtime only ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# curl: the container health check. libgssapi: Npgsql probes for it on connect (noisy without it).
# fontconfig + DejaVu: PDFium renders the first page of uploaded PDFs; text needs real glyphs.
# ca-certificates: outbound HTTPS (SMTP with STARTTLS/SSL when a mail provider is configured).
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl libgssapi-krb5-2 fontconfig fonts-dejavu-core ca-certificates \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app ./

# The official runtime image ships a non-root user "app" (UID 1654). Uploads are written to a bind mount the
# host chowns to that UID (deploy/README.md); the application directory itself stays read-only to it.
USER app

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_gcServer=0
EXPOSE 8080

HEALTHCHECK --interval=15s --timeout=5s --start-period=20s --retries=4 \
    CMD curl -fsS http://localhost:8080/health/live || exit 1

ENTRYPOINT ["dotnet", "Meydan.Api.dll"]
