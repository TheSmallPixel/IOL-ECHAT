# ECHAT: Enterprise E2EE Chat System

Sistema di messaggistica enterprise con crittografia end-to-end (E2EE). Il server non può leggere il contenuto dei messaggi.

## Prerequisiti

- [.NET 11 SDK](https://dotnet.microsoft.com/download/dotnet/11.0)
- [MySQL](https://dev.mysql.com/downloads/) 

## Setup

```bash
# 1. Clona il repository
git clone https://github.com/TheSmallPixel/ECHAT.git
cd ECHAT

# 2. Ripristina i pacchetti NuGet
dotnet restore ECHAT.slnx

# 3. Compila la soluzione
dotnet build ECHAT.slnx

# 4. Installa EF Core tools (una sola volta, globale)
dotnet tool install --global dotnet-ef

# 5. Crea/aggiorna il database MySQL (applica le migrazioni pendenti)
dotnet ef database update --project src/ECHAT.Server.App/ECHAT.Server.App.csproj

# (Solo quando cambi il modello) genera una nuova migration
dotnet ef migrations add AddSomethingNew --project src/ECHAT.Server.App/ECHAT.Server.App.csproj --output-dir Data/Migrations

# Secrets: Usa .NET user-secrets in development (dev only)
# JWT secret: dotnet user-secrets set "Authentication:Jwt:Secret" "your-dev-secret" --project src/ECHAT.Server.App
# Google ClientSecret: dotnet user-secrets set "Authentication:Google:ClientSecret" "your-google-secret" --project src/ECHAT.Server.App
# In production, use environment variables (Authentication__Jwt__Secret, Authentication__Google__ClientSecret, etc.)
```

## Esecuzione

```bash
# Avvia il server (API + Blazor WASM client)
dotnet run --project src/ECHAT.Server.App
```

## Test

```bash
# Esegui tutti i test
dotnet test ECHAT.slnx
```

## Copertura
```bash
dotnet tool install -g dotnet-reportgenerator-globaltool
dotnet test --settings coverage.runsettings --collect:"XPlat Code Coverage" --results-directory ./coverage
reportgenerator -reports:coverage/**/coverage.cobertura.xml -targetdir:coverage/report -reporttypes:"Html;TextSummary"
```

## Struttura soluzione

```
ECHAT/
├── src/
│   ├── ECHAT.Models/           # DTO, enum, interfacce (zero dipendenze)
│   ├── ECHAT.Client.Core/     # Logica client (crypto, chain, outbox)
│   ├── ECHAT.Server.Core/     # Logica server (pipeline, sequenze, saga)
│   ├── ECHAT.Client.App/     # Blazor WASM PWA
│   └── ECHAT.Server.App/     # ASP.NET Core API + EF Core + MySQL + SignalR
│
└── tests/
    ├── ECHAT.Client.Core.Tests/
    ├── ECHAT.Server.Core.Tests/
    ├── ECHAT.Integration.Tests/
    └── js/                        # test Node della crittografia WebCrypto (echat-crypto.mjs)
```

## Documentazione

La documentazione completa dell'architettura è disponibile nel sito Docusaurus:

```bash
cd docs-site
npm install
npm start
npm run build
npm run serve
```