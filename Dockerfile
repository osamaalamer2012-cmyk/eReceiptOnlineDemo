# build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore
RUN dotnet publish -c Release -o /out

# run
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /out .
# DO NOT set ASPNETCORE_URLS to 8080 here; the app binds to Render's PORT dynamically.
EXPOSE 8080
ENTRYPOINT ["dotnet", "eReceiptOnlineDemo.dll"]

