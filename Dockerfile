FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY Directory.Build.props ./
COPY src ./src
RUN dotnet publish src/Claims.Api -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
USER root
RUN mkdir -p /app/data && chown app:app /app/data
USER app
ENV ASPNETCORE_HTTP_PORTS=8080 Claims__DatabasePath=/app/data/claims.db
EXPOSE 8080
ENTRYPOINT ["dotnet", "Claims.Api.dll"]
