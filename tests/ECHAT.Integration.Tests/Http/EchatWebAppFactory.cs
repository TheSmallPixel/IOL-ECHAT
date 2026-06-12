using System.Linq;
using System.Net.Http.Headers;
using System.Text;
using ECHAT.Server.App.Data;
using ECHAT.Server.Core.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace ECHAT.Integration.Tests.Http;

/// <summary>
/// WebApplicationFactory che boota l'intera app Server.App (controllers + auth filters + ingest
/// pipeline + CoreExceptionFilter) al confine HTTP REALE, sostituendo solo il DbContext con
/// InMemory. Usiamo <see cref="EchatDbContext"/> come TEntryPoint perché è un tipo PUBBLICO
/// nell'assembly Server.App (Program è internal); WebApplicationFactory richiede solo che T viva
/// nell'assembly di entry-point, non che sia il Program stesso.
///
/// Boot config fornita via ConfigureAppConfiguration (ciò che Program.cs richiede all'avvio):
///   - Authentication:Jwt:Secret   segreto HS256 fisso ≥48 char
///   - Authentication:Google:ClientId/ClientSecret  dummy non-vuoti (login Google opzionale)
///   - Database:AutoMigrate=false   Program salta MigrateAsync (l'InMemory non va migrato)
/// L'ambiente è "Testing": non-Production (niente throw su CORS/HSTS) e non-Development
/// (niente OpenApi/auto-migrate by default). La chiave di firma JWT effettiva del middleware è
/// riallineata via PostConfigure&lt;JwtBearerOptions&gt; (vedi nota in ConfigureTestServices).
/// </summary>
public sealed class EchatWebAppFactory : WebApplicationFactory<EchatDbContext>
{
    // ≥48 char, stabile per-factory: lo stesso segreto firma il JWT lato test e valida lato server.
    public const string JwtSecret = "echat-e2e-test-signing-secret-0123456789-abcdefghijklmnop";
    public const string JwtIssuer = "ECHAT";
    public const string JwtAudience = "ECHAT";

    // Nome DB InMemory stabile per-factory: tutte le richieste della stessa factory condividono i dati.
    private readonly string _dbName = "e2e-" + Guid.NewGuid().ToString("N");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // Content root sul progetto Server.App: serve gli static web assets / index.html reali,
        // così UseBlazorFrameworkFiles/MapFallbackToFile non esplodono durante il boot.
        builder.UseContentRoot(ServerAppContentRoot());

        // Config di test in-memory: governa i valori letti DOPO il build (es. Database:AutoMigrate,
        // Google ClientId/Secret per la registrazione dello schema). NOTA: NON basta per il segreto
        // JWT: Program.cs cattura IssuerSigningKey leggendo il segreto a build-time, quando sotto
        // WebApplicationFactory è ancora attivo lo user-secret REALE dell'assembly Server.App. Quel
        // caso è gestito col PostConfigure<JwtBearerOptions> più sotto (eseguito a registrazione
        // completata). Qui forniamo comunque i valori per coerenza dell'IConfiguration runtime.
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Jwt:Secret"] = JwtSecret,
                ["Authentication:Jwt:Issuer"] = JwtIssuer,
                ["Authentication:Jwt:Audience"] = JwtAudience,
                ["Authentication:Google:ClientId"] = "dummy-google-client-id",
                ["Authentication:Google:ClientSecret"] = "dummy-google-client-secret",
                ["Database:AutoMigrate"] = "false",
            });
        });

        builder.ConfigureTestServices(services =>
        {
            // Rimuovi OGNI registrazione del DbContext MySQL (DbContextOptions<EchatDbContext> +
            // l'eventuale IDbContextOptionsConfiguration<> registrato da AddDbContext) e ri-aggiungi
            // InMemory con un nome DB stabile per-factory.
            RemoveDbContextRegistrations(services);

            services.AddDbContext<EchatDbContext>(o => o.UseInMemoryDatabase(_dbName));

            // Program.cs cattura IssuerSigningKey dal segreto letto a build-time (che, sotto
            // WebApplicationFactory, è ancora lo user-secret REALE dello sviluppatore: i nostri
            // override di config arrivano troppo tardi per QUELLA lettura). ConfigureTestServices
            // gira DOPO tutte le registrazioni di Program, quindi un PostConfigure sovrascrive in
            // modo deterministico i TokenValidationParameters con la NOSTRA chiave di test, così il
            // bearer middleware valida i token che firmiamo nei test. Niente modifiche a src/.
            services.PostConfigure<JwtBearerOptions>(
                JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    options.TokenValidationParameters.ValidIssuer = JwtIssuer;
                    options.TokenValidationParameters.ValidAudience = JwtAudience;
                    options.TokenValidationParameters.IssuerSigningKey =
                        new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtSecret));
                });
        });
    }

    private static void RemoveDbContextRegistrations(IServiceCollection services)
    {
        var toRemove = services.Where(d =>
            d.ServiceType == typeof(DbContextOptions<EchatDbContext>) ||
            d.ServiceType == typeof(DbContextOptions) ||
            (d.ServiceType.IsGenericType &&
             d.ServiceType.GetGenericTypeDefinition().Name.StartsWith("IDbContextOptionsConfiguration")) ||
            d.ServiceType == typeof(EchatDbContext))
            .ToList();

        foreach (var d in toRemove)
            services.Remove(d);
    }

    private static string ServerAppContentRoot()
    {
        // Risali dalla bin dei test fino alla root del repo, poi punta a src/ECHAT.Server.App.
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src", "ECHAT.Server.App")))
            dir = Directory.GetParent(dir)?.FullName;

        return dir is null
            ? AppContext.BaseDirectory
            : Path.Combine(dir, "src", "ECHAT.Server.App");
    }

    /// <summary>
    /// HttpClient con un JWT HS256 valido (stesso segreto/issuer/audience del server) per
    /// <paramref name="userId"/>. Le claim rispecchiano JwtTokenService: NameIdentifier + email + name.
    /// </summary>
    public HttpClient AuthedClient(Guid userId, string? email = null, string? displayName = null)
    {
        var client = CreateClient();
        var token = MintJwt(userId, email ?? $"{userId:N}@e2e.test", displayName ?? "E2E User");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>HttpClient anonimo (nessun Authorization header).</summary>
    public HttpClient AnonymousClient() => CreateClient();

    private static string MintJwt(Guid userId, string email, string displayName)
    {
        var jwt = new JwtTokenService();
        var result = jwt.GenerateToken(
            new ECHAT.Server.Core.Interfaces.UserRecord
            {
                Id = userId,
                GoogleSubjectId = "google-" + userId.ToString("N"),
                Email = email,
                DisplayName = displayName,
                PictureUrl = null,
                PlatformRole = "User",
            },
            new JwtTokenOptions
            {
                Secret = JwtSecret,
                Issuer = JwtIssuer,
                Audience = JwtAudience,
            });
        return result.Token;
    }
}
