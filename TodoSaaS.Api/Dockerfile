# Runtime Stage (Chiseled Ubuntu: non-root, ultra-minimal attack surface)
FROM mcr.microsoft.com/dotnet/aspnet:9.0-noble-chiseled AS final
WORKDIR /app

# Copy pre-published binaries
COPY publish/ .

# Run as non-root unprivileged app user
USER $APP_UID
EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080

ENTRYPOINT ["dotnet", "TodoSaaS.Api.dll"]
