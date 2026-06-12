using ECHAT.Server.App.Data;
using ECHAT.Server.App.Data.Entities;
using ECHAT.Server.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace ECHAT.Integration.Tests.Http;

/// <summary>
/// Inserisce dati nel DB InMemory condiviso della factory risolvendo i service scoped da
/// <c>factory.Services.CreateScope()</c>. Inserisce direttamente le entity EF (forme lette da
/// EchatDbContext / *Entity) così gli endpoint hanno conversazioni, membership e device registrati.
/// </summary>
public sealed class Seeder
{
    private readonly EchatWebAppFactory _factory;

    public Seeder(EchatWebAppFactory factory) => _factory = factory;

    /// <summary>Crea una UserEntity (necessaria per le query che fanno join Members↔Users).</summary>
    public Guid SeedUser(Guid? userId = null, string? email = null, string? displayName = null)
    {
        var id = userId ?? Guid.NewGuid();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EchatDbContext>();
        if (!db.Users.Any(u => u.Id == id))
        {
            db.Users.Add(new UserEntity
            {
                Id = id,
                GoogleSubjectId = "google-" + id.ToString("N"),
                Email = email ?? $"{id:N}@e2e.test",
                DisplayName = displayName ?? "E2E User",
                CreatedAt = DateTime.UtcNow,
                LastLoginAt = DateTime.UtcNow,
                IsActive = true,
                PlatformRole = "User",
            });
            db.SaveChanges();
        }
        return id;
    }

    /// <summary>
    /// Crea una conversazione con <paramref name="ownerId"/> come Owner e i membri extra indicati
    /// (default ruolo "Member"). Crea anche le UserEntity corrispondenti e inizializza il seq counter
    /// così i lease/seq partono da 0. Ritorna il conversationId.
    /// </summary>
    public Guid SeedConversation(Guid ownerId, int epochId = 1, params (Guid userId, string role)[] members)
    {
        var conversationId = Guid.NewGuid();
        SeedUser(ownerId);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EchatDbContext>();

        db.Conversations.Add(new ConversationEntity
        {
            Id = conversationId,
            Name = "E2E Conversation",
            CreatedByUserId = ownerId,
            CurrentEpochId = epochId,
            CreatedAt = DateTime.UtcNow,
        });

        db.Members.Add(new MemberEntity
        {
            ConversationId = conversationId,
            UserId = ownerId,
            Role = "Owner",
            JoinedAt = DateTime.UtcNow,
        });

        db.SeqCounters.Add(new SeqCounterEntity
        {
            ConversationId = conversationId,
        });

        db.SaveChanges();

        foreach (var (userId, role) in members)
            AddMember(conversationId, userId, role);

        return conversationId;
    }

    /// <summary>Aggiunge un membro attivo a una conversazione esistente (crea anche la UserEntity).</summary>
    public void AddMember(Guid conversationId, Guid userId, string role = "Member")
    {
        SeedUser(userId);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EchatDbContext>();
        db.Members.Add(new MemberEntity
        {
            ConversationId = conversationId,
            UserId = userId,
            Role = role,
            JoinedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
    }

    /// <summary>
    /// Registra le chiavi pubbliche di un <see cref="SigningDevice"/> nella directory, legandolo a
    /// <paramref name="userId"/> (mirror di ciò che fa DeviceDirectoryService.RegisterAsync dal JWT).
    /// </summary>
    public void RegisterDevice(Guid userId, SigningDevice device)
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevicePublicKeyStore>();
        store.UpsertAsync(new DevicePublicKeyRecord(
            userId, device.DeviceId, device.RsaOaepSpki, device.EcdsaSpki, DateTime.UtcNow))
            .GetAwaiter().GetResult();
    }

    /// <summary>Seed di un membership ORFANO: la membership esiste ma la ConversationEntity no.</summary>
    public Guid SeedMembershipWithoutConversation(Guid userId)
    {
        var conversationId = Guid.NewGuid();
        SeedUser(userId);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EchatDbContext>();
        db.Members.Add(new MemberEntity
        {
            ConversationId = conversationId,
            UserId = userId,
            Role = "Owner",
            JoinedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
        return conversationId;
    }
}
