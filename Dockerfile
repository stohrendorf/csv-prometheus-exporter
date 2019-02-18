FROM microsoft/dotnet:sdk AS build-env
WORKDIR /app

# Copy csproj and restore as distinct layers
COPY *.csproj ./
RUN dotnet restore

# Copy everything else and build
COPY . ./
RUN dotnet publish -c Release -o out

# Build runtime image
FROM microsoft/dotnet:2.1-aspnetcore-runtime-bionic
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        curl python3 python3-pip python3-yaml \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build-env /app/out .
EXPOSE 5000
ENTRYPOINT ["dotnet", "csv-prometheus-exporter.dll"]
