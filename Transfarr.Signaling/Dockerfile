# Use ASP.NET runtime for final image
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 5135

# Use SDK for building
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project files first for better caching
COPY ["Transfarr.Signaling/Transfarr.Signaling.csproj", "Transfarr.Signaling/"]
COPY ["Transfarr.Shared/Transfarr.Shared.csproj", "Transfarr.Shared/"]

# Restore dependencies
RUN dotnet restore "Transfarr.Signaling/Transfarr.Signaling.csproj"

# Copy the rest of the source code
COPY . .

# Build and publish
WORKDIR "/src/Transfarr.Signaling"
RUN dotnet build "Transfarr.Signaling.csproj" -c Release -o /app/build
RUN dotnet publish "Transfarr.Signaling.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Final runtime image
FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Transfarr.Signaling.dll"]
