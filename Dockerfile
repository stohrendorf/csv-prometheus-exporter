FROM microsoft/dotnet:sdk AS build-env
WORKDIR /app

COPY *.csproj ./
RUN dotnet restore

COPY . ./
RUN dotnet publish -c Release -o out


FROM microsoft/dotnet:2.1-aspnetcore-runtime-bionic
LABEL maintainer="Steffen Ohrendorf <steffen.ohrendorf@gmx.de>"

# Stuff not strictly necessary, but helps to write dynamic inventory scripty,
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
