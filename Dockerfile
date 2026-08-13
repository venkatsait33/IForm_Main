# ---- Build stage ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY IFormQualityApp.csproj ./
RUN dotnet restore "IFormQualityApp.csproj"

COPY . .
RUN dotnet publish "IFormQualityApp.csproj" -c Release -o /app/publish --no-restore

# ---- Runtime stage ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

COPY --from=build /app/publish .

# Render sets $PORT at runtime; Program.cs reads it and binds to it.
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 10000

ENTRYPOINT ["dotnet", "IFormQualityApp.dll"]
