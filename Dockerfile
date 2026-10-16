FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY DemirbasYonetimProje.csproj ./
RUN dotnet restore DemirbasYonetimProje.csproj

COPY . .
RUN dotnet publish DemirbasYonetimProje.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish ./
COPY --from=build /src/frontend ./frontend

ENV ASPNETCORE_URLS=http://+:5181
EXPOSE 5181
USER app

ENTRYPOINT ["dotnet", "DemirbasYonetimProje.dll"]
