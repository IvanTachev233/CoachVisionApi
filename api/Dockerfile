# syntax=docker/dockerfile:1
# The Vision API image. Base images are pinned by digest; Dependabot moves the digests.
# Build from api/: docker build -t vision-api api

# Stage 1: restore and publish. Runs on the builder's own platform: the output is
# portable .NET, so the same files run on any platform the runtime image has.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0@sha256:fe0fd4e93bd2fbb47c883b7a6369e5c8d3db8bf01cdc5981f6ee35fbdf3ff4fb AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/CoachVision.Api/CoachVision.Api.csproj src/CoachVision.Api/packages.lock.json src/CoachVision.Api/
# Exactly the locked packages; the restore also runs the vulnerable-package audit.
RUN dotnet restore src/CoachVision.Api --locked-mode
COPY src/ src/
RUN dotnet publish src/CoachVision.Api --configuration Release --no-restore --output /app

# Stage 2: runtime. Chiseled Ubuntu: the ASP.NET Core runtime and little else, no shell or
# package manager, and the unprivileged `app` user (uid 1654) already set as the user.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled@sha256:7c625e44e5d588b4d7b2a913e94ca4bd5098ce47442da404829e4c7936a764e1 AS production
WORKDIR /app
# The files stay owned by root, so the process can read the app but not change it.
COPY --from=build /app ./
USER $APP_UID
# The image listens on 8080 (ASPNETCORE_HTTP_PORTS), the port Cloud Run sends requests to.
EXPOSE 8080
ENTRYPOINT ["dotnet", "CoachVision.Api.dll"]
