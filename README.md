# ECHAT: Enterprise E2EE Chat System

> **[Read the documentation](https://TheSmallPixel.github.io/IOL-ECHAT/)**

> **University project** built for academic purposes as a thesis project. It is a learning-oriented reference implementation, not a production-hardened product.

Enterprise messaging system with end-to-end encryption (E2EE). The server cannot read message contents: all encryption and decryption happen on the client, and the server only stores ciphertext, wrapped keys, signatures, and metadata.

## Prerequisites

- [.NET 11 SDK](https://dotnet.microsoft.com/download/dotnet/11.0)
- [MySQL](https://dev.mysql.com/downloads/)

## Setup

```bash
# 1. Clone the repository
git clone https://github.com/TheSmallPixel/IOL-ECHAT.git
cd ECHAT

# 2. Restore NuGet packages
dotnet restore ECHAT.slnx

# 3. Build the solution
dotnet build ECHAT.slnx

# 4. Install EF Core tools (once, globally)
dotnet tool install --global dotnet-ef

# 5. Create/update the MySQL database (apply pending migrations)
dotnet ef database update --project src/ECHAT.Server.App/ECHAT.Server.App.csproj

# (Only when you change the model) generate a new migration
dotnet ef migrations add AddSomethingNew --project src/ECHAT.Server.App/ECHAT.Server.App.csproj --output-dir Data/Migrations

# Secrets: use .NET user-secrets in development (dev only)
# JWT secret: dotnet user-secrets set "Authentication:Jwt:Secret" "your-dev-secret" --project src/ECHAT.Server.App
# Google ClientSecret: dotnet user-secrets set "Authentication:Google:ClientSecret" "your-google-secret" --project src/ECHAT.Server.App
# In production, use environment variables (Authentication__Jwt__Secret, Authentication__Google__ClientSecret, etc.)
```

## Running

```bash
# Start the server (API + Blazor WASM client)
dotnet run --project src/ECHAT.Server.App
```

## Tests

```bash
# Run all tests
dotnet test ECHAT.slnx
```

## Coverage

```bash
dotnet tool install -g dotnet-reportgenerator-globaltool
dotnet test --settings coverage.runsettings --collect:"XPlat Code Coverage" --results-directory ./coverage
reportgenerator -reports:coverage/**/coverage.cobertura.xml -targetdir:coverage/report -reporttypes:"Html;TextSummary"
```

## Solution structure

```
ECHAT/
├── src/
│   ├── ECHAT.Models/           # DTOs, enums, interfaces (zero dependencies)
│   ├── ECHAT.Client.Core/      # Client logic (crypto, chain, outbox)
│   ├── ECHAT.Server.Core/      # Server logic (pipeline, sequences, saga)
│   ├── ECHAT.Client.App/       # Blazor WASM PWA
│   └── ECHAT.Server.App/       # ASP.NET Core API + EF Core + MySQL + SignalR
│
└── tests/
    ├── ECHAT.Client.Core.Tests/
    ├── ECHAT.Server.Core.Tests/
    ├── ECHAT.Integration.Tests/
    └── js/                      # Node tests for the WebCrypto crypto (echat-crypto.mjs)
```

## Documentation

The full architecture documentation is available as a Docusaurus site, published to GitHub Pages:

**https://TheSmallPixel.github.io/IOL-ECHAT/**

To run or build it locally:

```bash
cd docs-site
npm install
npm start        # local dev server
npm run build    # production build
npm run serve    # serve the production build
```
