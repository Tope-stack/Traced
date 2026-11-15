# syntax=docker/dockerfile:1
# Build from the repository root:  docker build -f src/Traced.Api/Dockerfile -t traced-api .

# 1. Publish the Api. global.json is not copied: it pins a local SDK feature band (10.0.400) that the
#    image's SDK may not match, and the image already provides a .NET 10 SDK.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props .editorconfig ./
COPY src/ src/
# Embedded into the Api for /openapi.yaml and /docs.
COPY spec/ spec/
RUN dotnet publish src/Traced.Api -c Release -o /app

# 2. Runtime: non-root, port 8080. Program.cs binds 0.0.0.0:8080 unless ASPNETCORE_URLS is set.
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "Traced.Api.dll"]
