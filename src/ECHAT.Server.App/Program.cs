using System.Text;
using ECHAT.Server.App.Data;
using ECHAT.Server.App.Hubs;
using ECHAT.Server.App.Middleware;
using ECHAT.Server.App.Repositories;
using ECHAT.Server.App.Services;
using ECHAT.Server.Core.Interfaces;
using ECHAT.Server.Core.Pipeline;
using ECHAT.Server.Core.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddUserSecrets("4f71aa5c-d35d-4e96-b223-f9c568ac4919");

{
    var appData = Environment.GetEnvironmentVariable("APPDATA");
    var secretsPath = string.IsNullOrEmpty(appData)
        ? "(APPDATA non impostata)"
        : Path.Combine(appData, "Microsoft", "UserSecrets", "4f71aa5c-d35d-4e96-b223-f9c568ac4919", "secrets.json");
    var exists = appData is not null && File.Exists(secretsPath);
    var hasKey = false;
    if (exists)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(secretsPath));
            hasKey = doc.RootElement.TryGetProperty("Authentication:Google:ClientSecret", out var v)
                     && !string.IsNullOrWhiteSpace(v.GetString());
        }
        catch (Exception ex) { Console.WriteLine($"[startup] UserSecrets parse error: {ex.Message}"); }
    }
    Console.WriteLine($"[startup] UserSecrets: path={secretsPath}; exists={exists}; hasClientSecretKey={hasKey}");
}


// EF Core + MySQL
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Server=localhost;Database=echat;User=root;Password=;";
builder.Services.AddDbContext<EchatDbContext>(options =>
    options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString)));

// Controller + SignalR (con filtro globale che mappa NotFound/Forbidden/Conflict dei service di Core)
builder.Services.AddControllers(options =>
{
    options.Filters.Add<ECHAT.Server.App.Middleware.CoreExceptionFilter>();
});
builder.Services.AddSignalR();
// OpenAPI nativo di ASP.NET Core (sostituisce Swashbuckle; produce /openapi/v1.json in dev).
builder.Services.AddOpenApi();
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient();

// Interfacce di Server.Core mappate sulle implementazioni della App
builder.Services.AddScoped<IMessageRepository, MessageRepository>();
builder.Services.AddScoped<IKeyEnvelopeStore, KeyEnvelopeRepository>();
builder.Services.AddScoped<IAuditLog, AuditLogRepository>();
builder.Services.AddScoped<IRealtimeNotifier, SignalRNotifier>();
builder.Services.AddScoped<IMembershipReader, MembershipReader>();
builder.Services.AddScoped<IConversationReader, ConversationReader>();
builder.Services.AddScoped<ISeqCounterStore, SeqCounterStore>();
builder.Services.AddScoped<ISeqLeaseStore, SeqLeaseStore>();
builder.Services.AddScoped<IMigrationJobStore, MigrationJobStore>();
builder.Services.AddScoped<IChainBoundaryStore, ChainBoundaryStore>();
builder.Services.AddScoped<IConversationStore, ConversationStore>();
builder.Services.AddScoped<IConversationPurger, ConversationPurger>();
builder.Services.AddScoped<IMemberStore, MemberStore>();
builder.Services.AddScoped<IUserStore, UserStore>();
builder.Services.AddScoped<IDevicePublicKeyStore, DevicePublicKeyStore>();
builder.Services.AddScoped<ECHAT.Server.Core.Services.DeviceDirectoryService>();
builder.Services.AddSingleton<ECHAT.Server.Core.Services.BlobFileAssemblyService>();
builder.Services.AddSingleton<IBlobStorageService, BlobFileSystemStorage>();

// Service di Server.Core
builder.Services.AddScoped<ISequenceService, ECHAT.Server.Core.Services.SequenceService>();
builder.Services.AddScoped<IMigrationOrchestrator, ECHAT.Server.Core.Services.MigrationOrchestratorService>();
builder.Services.AddScoped<IPolicyEnforcer, PolicyEnforcer>();
builder.Services.AddScoped<ECHAT.Server.Core.Services.KeyAccessService>();
builder.Services.AddSingleton<ECHAT.Server.Core.Services.JwtTokenService>();
builder.Services.AddScoped<ITombstoneInjectionService, ECHAT.Server.Core.Services.TombstoneInjectionService>();
builder.Services.AddSingleton<ECHAT.Server.Core.Services.UserUpsertService>();
builder.Services.AddSingleton<IAiReplyService, ECHAT.Server.Core.Services.AiReplyService>();
builder.Services.AddSingleton<ECHAT.Server.Core.Services.AvatarService>();
builder.Services.AddScoped<IUserSearchService, ECHAT.Server.Core.Services.UserSearchService>();
builder.Services.AddScoped<ECHAT.Server.Core.Services.ConversationOperationsService>();
builder.Services.AddScoped<ECHAT.Server.Core.Services.MessageModerationService>();
builder.Services.AddScoped<ECHAT.Server.Core.Services.PlatformStatisticsService>();
builder.Services.AddScoped<ECHAT.Server.Core.Services.UserSyncService>();
builder.Services.AddSingleton<QuotaService>();
builder.Services.AddSingleton<IEnvelopeValidator, EnvelopeValidator>();
builder.Services.AddSingleton<SeqCounterDomainService>();

// Retention sweep (lease scaduti + audit log vecchio)
builder.Services.AddHostedService<RetentionBackgroundService>();

// Health check: liveness + readiness EF
builder.Services.AddHealthChecks()
    .AddDbContextCheck<EchatDbContext>("database");

// Handler della pipeline di ingest (l'ordine conta: Chain of Responsibility)
// Nota: non c'è un handler "seq deve essere > anchor": rifiuterebbe arrivi legittimi fuori ordine
// da sender concorrenti con lease disgiunti. Il vincolo UNIQUE(ConversationId, Seq) del DB
// è l'unica fonte autoritativa per la deduplication per seq.
// PRIMO: lega l'identità del mittente al JWT + directory device (S4), prima di ogni altra cosa.
builder.Services.AddScoped<IIngestHandler, SenderIdentityHandler>();
builder.Services.AddScoped<IIngestHandler, LeaseValidationHandler>();
// Verifica la firma ECDSA del mittente (S3) PRIMA della deduplication: ogni envelope è verificato
// crittograficamente prima di qualunque altro effetto, e non si spreca lavoro di dedup su forgeri.
builder.Services.AddScoped<IIngestHandler, SignatureVerificationHandler>();
builder.Services.AddScoped<IIngestHandler, DeduplicationHandler>();
builder.Services.AddScoped<IIngestHandler, PersistHandler>();
builder.Services.AddScoped<IIngestHandler, NotifyHandler>();
builder.Services.AddScoped<IIngestHandler, AuditHandler>();
builder.Services.AddScoped<IMessageIngestPipeline, MessageIngestPipeline>();

// Autenticazione: JWT bearer (default) + OAuth Google (login esterno)
var jwtSection = builder.Configuration.GetSection("Authentication:Jwt");
var jwtSecret = jwtSection["Secret"];
if (string.IsNullOrWhiteSpace(jwtSecret))
    throw new InvalidOperationException(
        "Authentication:Jwt:Secret is required (32+ chars). Set it via user-secrets (dev: dotnet user-secrets set \"Authentication:Jwt:Secret\" <value>) or the Authentication__Jwt__Secret env var (prod). Do NOT commit it to appsettings.json.");
if (jwtSecret.Length < 32)
    throw new InvalidOperationException(
        "Authentication:Jwt:Secret must be at least 32 characters (HS256 minimum security).");

var authBuilder = builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
})
.AddCookie(options =>
{
    options.Cookie.Name = "ECHAT_ExternalAuth";
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.HttpOnly = true;
    options.ExpireTimeSpan = TimeSpan.FromMinutes(5);
})
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = jwtSection["Issuer"] ?? "ECHAT",
        ValidAudience = jwtSection["Audience"] ?? "ECHAT",
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret))
    };
    // I browser non possono mettere header custom sull'handshake WebSocket: il client SignalR
    // passa quindi il JWT come query string `access_token` sulle URL `/hubs/*`. Senza questo
    // hook l'utente sull'hub è anonimo, e `Clients.Users(...)` non riesce a indirizzarlo.
    options.Events = new JwtBearerEvents
    {
        OnMessageReceived = ctx =>
        {
            var accessToken = ctx.Request.Query["access_token"];
            var path = ctx.HttpContext.Request.Path;
            if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
                ctx.Token = accessToken;
            return Task.CompletedTask;
        }
    };
});

// Google OAuth è un login esterno OPZIONALE: registriamo lo schema SOLO se ClientId e
// ClientSecret sono presenti. AddGoogle esegue una validazione (ThrowIfNullOrEmpty) che,
// con secret vuoto, fa crollare OGNI richiesta con "ClientSecret cannot be an empty string".
// Senza secret il login Google è semplicemente non disponibile, ma l'app e l'auth JWT partono.
var googleSection = builder.Configuration.GetSection("Authentication:Google");
var googleClientId = googleSection["ClientId"];
var googleClientSecret = googleSection["ClientSecret"];
if (!string.IsNullOrWhiteSpace(googleClientId) && !string.IsNullOrWhiteSpace(googleClientSecret))
{
    authBuilder.AddGoogle(options =>
    {
        options.ClientId = googleClientId;
        options.ClientSecret = googleClientSecret;
        options.CallbackPath = "/api/auth/google-callback";
        options.Scope.Add("profile");
        options.ClaimActions.MapJsonKey("picture", "picture");
    });
    Console.WriteLine("[startup] Google OAuth ABILITATO: schema 'Google' registrato.");
}
else
{
    Console.WriteLine("[startup] Google OAuth disabilitato: Authentication:Google:ClientId/ClientSecret non configurati.");
}

// CORS: le origin arrivano da configurazione così in prod si limita ai veri host frontend.
// AllowCredentials() richiede una lista esplicita di origin (mai AllowAnyOrigin): il browser
// rifiuta `Access-Control-Allow-Origin: *` insieme alle credenziali, ma soprattutto consentire
// qualunque origin con i cookie/JWT aperti è una CSRF/credential-leak. In produzione NON
// permettiamo un fallback: se Cors:AllowedOrigins manca o è vuoto, falliamo all'avvio.
var configuredOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?.Where(o => !string.IsNullOrWhiteSpace(o))
    .ToArray() ?? Array.Empty<string>();

string[] allowedOrigins;
if (configuredOrigins.Length > 0)
{
    allowedOrigins = configuredOrigins;
}
else if (builder.Environment.IsProduction())
{
    throw new InvalidOperationException(
        "Cors:AllowedOrigins must be set in Production. Configure the explicit frontend origin(s) " +
        "(e.g. via appsettings.Production.json or the ECHAT_CORS__ALLOWEDORIGINS env var). " +
        "Refusing to start with an open/credentialed CORS policy.");
}
else
{
    // Default solo per sviluppo locale: HTTPS only (niente http:// con AllowCredentials).
    allowedOrigins = new[] { "https://localhost:5002" };
}

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins(allowedOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

var app = builder.Build();

// Applica all'avvio le migration EF in sospeso. Di default ON in Development, OFF in Production
// (override con `Database:AutoMigrate=true|false`). Evita l'errore "table doesn't exist" dopo aver
// fatto pull di un branch con una nuova migration senza ricordarsi di lanciare `dotnet ef database update`.
var autoMigrate = app.Configuration.GetValue<bool?>("Database:AutoMigrate")
                  ?? app.Environment.IsDevelopment();
if (autoMigrate)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<EchatDbContext>();
    var startupLogger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
    if (pending.Count > 0)
    {
        startupLogger.LogInformation("Applying {Count} pending migration(s): {Migrations}",
            pending.Count, string.Join(", ", pending));
        await db.Database.MigrateAsync();
    }
}

if (app.Environment.IsDevelopment())
{
    // Documento OpenAPI su /openapi/v1.json
    app.MapOpenApi();
    // UI interattiva di Scalar su /scalar/v1 (sostituisce Swagger UI).
    app.MapScalarApiReference();
}
else
{
    // Production: HSTS 1 anno così i browser rifiutano HTTP in chiaro.
    app.UseHsts();
}

app.UseHttpsRedirection();

// Correlation ID: avvolge ogni richiesta in uno scope di logging.
app.UseMiddleware<CorrelationIdMiddleware>();

// Security header su ogni risposta. In dev allarghiamo connect-src per browser-link + hot reload.
var isDev = app.Environment.IsDevelopment();
var connectSrc = isDev
    ? "connect-src 'self' ws: wss: http: https:"
    : "connect-src 'self' wss: https:";

app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    headers["Permissions-Policy"] = "geolocation=(), microphone=(), camera=()";

    // COOP/COEP/CORP servono solo con WasmEnableThreads attivo (qui non lo è - vedi
    // Client.App.csproj per le ragioni). Riaggiungere questi tre header quando il threading
    // sarà riabilitato in una futura release di .NET.

    headers["Content-Security-Policy"] =
        "default-src 'self'; " +
        "script-src 'self' 'unsafe-eval' 'wasm-unsafe-eval'; " +
        "style-src 'self' 'unsafe-inline'; " +
        // Gli allegati decifrati diventano blob: URL per la preview inline.
        "img-src 'self' data: blob: https://lh3.googleusercontent.com; " +
        "media-src 'self' blob:; " +
        // I PDF vengono renderizzati con <iframe src=blob:...>, quindi anche frame-src deve consentire blob:.
        "frame-src 'self' blob:; " +
        connectSrc + "; " +
        "font-src 'self' data:; " +
        "object-src 'none'; base-uri 'self'; frame-ancestors 'none'";
    await next();
});

// Serve il client Blazor WASM
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

app.UseCors();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHub<ChatHub>("/hubs/chat");

// Endpoint di health: /health (completo, DB incluso), /health/live (solo liveness, nessun check).
app.MapHealthChecks("/health");
app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false
});

// Fallback alla SPA client per le route non-API
app.MapFallbackToFile("index.html");

app.Run();
