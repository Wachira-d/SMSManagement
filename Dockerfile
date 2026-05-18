# Multi-stage build for the ASP.NET Core 8 campaign platform.
# Stage 1 — restore + publish a self-contained release into /out
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy only the project files first so NuGet restore is cached when source changes.
COPY SMSManagement/SMSManagement.csproj SMSManagement/
RUN dotnet restore SMSManagement/SMSManagement.csproj

# Copy the rest and publish.
COPY . .
RUN dotnet publish SMSManagement/SMSManagement.csproj \
    -c Release \
    -o /out \
    --no-restore \
    /p:UseAppHost=false

# Stage 2 — slim runtime image
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Run as a non-root user; the framework needs the user to exist before chowning files.
RUN groupadd --system app && useradd --system --gid app --uid 10001 app

COPY --from=build --chown=app:app /out ./

# Place for persisted Data Protection keys + Hangfire dashboard files (if any).
RUN mkdir -p /var/app/keys /var/app/uploads && chown -R app:app /var/app
VOLUME /var/app/keys

USER app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_USE_POLLING_FILE_WATCHER=1 \
    DataProtection__KeyDirectory=/var/app/keys

ENTRYPOINT ["dotnet", "SMSManagement.dll"]
