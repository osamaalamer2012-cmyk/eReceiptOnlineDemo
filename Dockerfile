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
# Do NOT set ASPNETCORE_URLS here; the app binds to PORT dynamically.
EXPOSE 8080
ENTRYPOINT ["dotnet", "eReceiptOnlineDemo.dll"]
