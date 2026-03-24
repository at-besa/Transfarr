# Use ASP.NET runtime for final image
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 5150
EXPOSE 5151

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
COPY --from=build /app/publish .

# Create persistent directories
RUN mkdir -p /app/data /app/downloads /app/shares

# Default environment for running in container
ENV ASPNETCORE_URLS=http://0.0.0.0:5150
ENTRYPOINT ["dotnet", "Transfarr.Node.dll"]
