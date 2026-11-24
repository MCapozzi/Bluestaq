FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Bluestaq.Notes.Api/Bluestaq.Notes.Api.csproj src/Bluestaq.Notes.Api/
RUN dotnet restore src/Bluestaq.Notes.Api/Bluestaq.Notes.Api.csproj
COPY src/ src/
RUN dotnet publish src/Bluestaq.Notes.Api/Bluestaq.Notes.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080 \
    AdminAudit__LogFilePath=/var/log/bluestaq/admin-audit-.log
# Writable by the non-root app user; named volumes mounted here inherit this ownership.
RUN mkdir -p /var/log/bluestaq && chown "$APP_UID" /var/log/bluestaq
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Bluestaq.Notes.Api.dll"]
