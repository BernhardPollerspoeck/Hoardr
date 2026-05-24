# syntax=docker/dockerfile:1

# ---- build -----------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first (cached unless a .csproj changes).
COPY src/Hoardr.Core/Hoardr.Core.csproj src/Hoardr.Core/
COPY src/Hoardr.Web/Hoardr.Web.csproj src/Hoardr.Web/
RUN dotnet restore src/Hoardr.Web/Hoardr.Web.csproj

# Build + publish the web app (pulls in Hoardr.Core).
# NOTE: do NOT pass --no-restore here. In .NET 10 blazor.web.js is no longer embedded in the
# assembly; it must be emitted to wwwroot/_framework at publish. A split restore + publish
# --no-restore skips the targets that generate the framework static assets, so blazor.web.js
# is missing from the image and Kestrel returns 404. See dotnet/aspnetcore#63962.
COPY . .
RUN dotnet publish src/Hoardr.Web/Hoardr.Web.csproj -c Release -o /app /p:UseAppHost=false

# ---- runtime ---------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Listen on 8080; keep all state (blobs + embedded DB) under /data.
ENV ASPNETCORE_HTTP_PORTS=8080 \
    Hoardr__DataRoot=/data
RUN mkdir -p /data
VOLUME /data
EXPOSE 8080

COPY --from=build /app ./

# Runs as root so a bind-mounted ./data "just works"; set a USER to harden.
ENTRYPOINT ["dotnet", "Hoardr.Web.dll"]
