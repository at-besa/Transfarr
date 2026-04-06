# Use ASP.NET runtime for final image
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 5150
EXPOSE 5151

# Install curl and adduser for healthcheck and user management
RUN apt-get update && apt-get install -y --no-install-recommends curl adduser && rm -rf /var/lib/apt/lists/*

# Use SDK for building
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy all relevant project files for better caching
COPY ["Transfarr.Node/Transfarr.Node.csproj", "Transfarr.Node/"]
COPY ["Transfarr.Shared/Transfarr.Shared.csproj", "Transfarr.Shared/"]
COPY ["Transfarr.Client.Web/Transfarr.Client.Web.csproj", "Transfarr.Client.Web/"]
COPY ["Transfarr.Client.Core/Transfarr.Client.Core.csproj", "Transfarr.Client.Core/"]

# Restore the Node project
RUN dotnet restore "Transfarr.Node/Transfarr.Node.csproj"

# Copy the rest of the source code
COPY . .

# Build and publish
WORKDIR "/src/Transfarr.Node"
RUN dotnet build "Transfarr.Node.csproj" -c Release -o /app/build
RUN dotnet publish "Transfarr.Node.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Final runtime image
FROM base AS final
WORKDIR /app

# Add a non-root user for security
RUN useradd -m -s /bin/bash transfarr

# Create persistent directories and set ownership
RUN mkdir -p /app/data /app/downloads /app/shares && \
    chown -R transfarr:transfarr /app

# Switch to non-root user
USER transfarr

COPY --from=build /app/publish .

# Define persistent volumes
VOLUME ["/app/data", "/app/downloads", "/app/shares"]

# Default environment for running in container
ENV ASPNETCORE_URLS=http://0.0.0.0:5150
ENV Transfarr__Storage__DatabasePath=/app/data/node.db
ENV Transfarr__Storage__DefaultDownloadsFolder=/app/downloads
ENV Transfarr__NodePort=5150
ENV Transfarr__Network__P2PPort=5151

# Healthcheck to verify the node is responsive
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
  CMD curl -f http://localhost:5150/health || exit 1

ENTRYPOINT ["dotnet", "Transfarr.Node.dll"]
