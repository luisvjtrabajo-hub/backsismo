FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["SismoAI.slnx", "./"]
COPY ["src/SismoAI.Api/SismoAI.Api.csproj", "src/SismoAI.Api/"]
COPY ["src/SismoAI.Application/SismoAI.Application.csproj", "src/SismoAI.Application/"]
COPY ["src/SismoAI.Domain/SismoAI.Domain.csproj", "src/SismoAI.Domain/"]
COPY ["src/SismoAI.Infrastructure/SismoAI.Infrastructure.csproj", "src/SismoAI.Infrastructure/"]
COPY ["src/SismoAI.Analytics/SismoAI.Analytics.csproj", "src/SismoAI.Analytics/"]

RUN dotnet restore "src/SismoAI.Api/SismoAI.Api.csproj"

COPY . .
RUN dotnet publish "src/SismoAI.Api/SismoAI.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

ENV ASPNETCORE_URLS=http://0.0.0.0:10000
ENV ASPNETCORE_ENVIRONMENT=Production

COPY --from=build /app/publish .

EXPOSE 10000

ENTRYPOINT ["dotnet", "SismoAI.Api.dll"]
