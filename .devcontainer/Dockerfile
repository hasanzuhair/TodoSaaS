FROM mcr.microsoft.com/devcontainers/dotnet:1-9.0

# Remove expired Yarn apt repository configuration to prevent GPG errors during feature installation
RUN rm -f /etc/apt/sources.list.d/yarn.list
