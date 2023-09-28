FROM mcr.microsoft.com/dotnet/sdk:7.0 AS build-env
WORKDIR /app

COPY *.csproj ./
RUN dotnet restore

COPY . ./
RUN dotnet test && dotnet publish -c Release -o out

FROM mcr.microsoft.com/dotnet/aspnet:7.0
LABEL maintainer="Steffen Ohrendorf <steffen.ohrendorf@gmx.de>"

# Stuff not strictly necessary, but helps to write dynamic inventory scripts,
# like retrieving a list of hosts from a web service from a python script.
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        curl python3 python3-pip python3-yaml \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build-env /app/out .

EXPOSE 5000
ENV ASPNETCORE_ENVIRONMENT="Production"
ENTRYPOINT ["dotnet", "csv-prometheus-exporter.dll"]

HEALTHCHECK --interval=5s --timeout=1s --start-period=10s \
    CMD curl http://localhost:5000/ping || exit 1
