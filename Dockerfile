FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY RaidDcBot.csproj ./
RUN dotnet restore RaidDcBot.csproj

COPY . ./
RUN dotnet publish RaidDcBot.csproj -c Release -o /app/publish /p:UseAppHost=false

# Install Playwright browsers in build stage (SDK has all tools)
ENV PLAYWRIGHT_BROWSERS_PATH=/app/publish/playwright-browsers
RUN dotnet tool install --global Microsoft.Playwright.CLI
ENV PATH="${PATH}:/root/.dotnet/tools"
RUN playwright install chromium

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app

# Playwright dependencies + existing ones
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
       fontconfig fonts-noto-cjk libopus0 \
       libglib2.0-0 libnss3 libnspr4 libdbus-1-3 \
       libatk1.0-0 libatk-bridge2.0-0 libcups2 libdrm2 \
       libxkbcommon0 libxcomposite1 libxdamage1 libxfixes3 \
       libxrandr2 libgbm1 libpango-1.0-0 libcairo2 \
       libasound2t64 libatspi2.0-0 \
    && rm -rf /var/lib/apt/lists/*

ENV DOTNET_ENVIRONMENT=Production
ENV PLAYWRIGHT_BROWSERS_PATH=/app/playwright-browsers

COPY --from=build /app/publish ./
COPY --from=build /app/publish/playwright-browsers /app/playwright-browsers

ENTRYPOINT ["dotnet", "RaidDcBot.dll"]
