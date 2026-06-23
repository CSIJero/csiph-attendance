# ----------------------------------------------------------------------------
# Multi-stage Dockerfile for ASP.NET Core 8 attendance monitoring app.
# Pinned to Microsoft's official .NET 8 images so the runtime matches the
# project's <TargetFramework>net8.0</TargetFramework>.
# ----------------------------------------------------------------------------

# Build stage — pulls in the SDK so dotnet restore + publish work.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore first as a separate layer so subsequent rebuilds with only source
# changes can reuse the cached NuGet packages.
COPY AttendanceMonitoring.csproj ./
RUN dotnet restore AttendanceMonitoring.csproj

# Copy the rest of the source and publish a Release build.
COPY . .
RUN dotnet publish AttendanceMonitoring.csproj \
        -c Release \
        -o /app \
        /p:UseAppHost=false

# ----------------------------------------------------------------------------
# Runtime stage — slimmer ASP.NET image, no SDK / build tools.
# ----------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# libgdiplus is required by System.Drawing.Common (Services/FaceHash.cs
# decodes check-in selfies via the GDI+ shim on Linux). Without it, the
# perceptual-hash routine throws TypeInitializationException on first
# call and check-ins silently fall back to FaceMatchStatus = "Unavailable".
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgdiplus libc6-dev \
    && rm -rf /var/lib/apt/lists/*

# Pull the published output across.
COPY --from=build /app ./

# Render maps the container's $PORT (default 10000) to public 443; binding to
# 0.0.0.0 is required for the host to be able to reach Kestrel.
#
# ConnectionStrings__DefaultConnection is injected at deploy time (see
# render.yaml — it's wired to the managed Postgres service). Locally, the
# image falls back to a SQLite file in /tmp so `docker run` still works for
# smoke tests.
ENV ASPNETCORE_URLS=http://0.0.0.0:10000 \
    ConnectionStrings__DefaultConnection="Data Source=/tmp/attendance.db" \
    DOTNET_PRINT_TELEMETRY_MESSAGE=false \
    DOTNET_NOLOGO=true

EXPOSE 10000

ENTRYPOINT ["dotnet", "AttendanceMonitoring.dll"]
