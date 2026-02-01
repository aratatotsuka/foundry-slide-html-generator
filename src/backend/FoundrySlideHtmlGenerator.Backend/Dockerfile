FROM mcr.microsoft.com/dotnet/sdk:8.0-jammy AS build
WORKDIR /src

COPY FoundrySlideHtmlGenerator.sln ./
COPY src/backend/FoundrySlideHtmlGenerator.Backend/FoundrySlideHtmlGenerator.Backend.csproj src/backend/FoundrySlideHtmlGenerator.Backend/
RUN dotnet restore FoundrySlideHtmlGenerator.sln

COPY src/backend/FoundrySlideHtmlGenerator.Backend/ src/backend/FoundrySlideHtmlGenerator.Backend/
COPY seed-data/ seed-data/

RUN dotnet publish src/backend/FoundrySlideHtmlGenerator.Backend/FoundrySlideHtmlGenerator.Backend.csproj -c Release -o /app/publish --no-restore

# Runtime image with Playwright browsers + deps.
# Keep version in sync with Microsoft.Playwright package version (currently 1.57.0).
FROM mcr.microsoft.com/playwright/dotnet:v1.57.0-jammy
WORKDIR /app

COPY --from=build /app/publish/ ./
COPY --from=build /src/seed-data/ ./seed-data/

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "FoundrySlideHtmlGenerator.Backend.dll"]

