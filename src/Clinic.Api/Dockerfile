# Clinic API — build from the repository root:
#   docker build -f src/Clinic.Api/Dockerfile -t clinic-api .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Clinic.Shared/Clinic.Shared.csproj src/Clinic.Shared/
COPY src/Clinic.Domain/Clinic.Domain.csproj src/Clinic.Domain/
COPY src/Clinic.Application/Clinic.Application.csproj src/Clinic.Application/
COPY src/Clinic.Infrastructure/Clinic.Infrastructure.csproj src/Clinic.Infrastructure/
COPY src/Clinic.Api/Clinic.Api.csproj src/Clinic.Api/
RUN dotnet restore src/Clinic.Api/Clinic.Api.csproj
COPY src/ src/
RUN dotnet publish src/Clinic.Api/Clinic.Api.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
# Attachments live on a volume (Files:Root); the app runs as the image's non-root user.
ENV ASPNETCORE_URLS=http://+:8080 \
    Files__Root=/data/files \
    DOTNET_gcServer=1
RUN mkdir -p /data/files && chown -R app:app /data
COPY --from=build /app .
USER app
EXPOSE 8080
# Liveness: GET /health (probe it from the proxy or orchestrator; the runtime image has no curl/wget).
ENTRYPOINT ["dotnet", "Clinic.Api.dll"]
