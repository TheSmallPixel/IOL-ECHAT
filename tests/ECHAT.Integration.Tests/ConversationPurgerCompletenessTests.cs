using System.Collections;
using ECHAT.Models.Dtos;
using ECHAT.Server.App.Data;
using ECHAT.Server.App.Data.Entities;
using ECHAT.Server.App.Repositories;
using ECHAT.Server.Core.Interfaces;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace ECHAT.Integration.Tests;

/// <summary>
/// Guardia di completezza per il crypto-shred (<see cref="ConversationPurger"/>).
///
/// Il purger enumera a mano i DbSet da cancellare per ConversationId. Non c'è nulla che garantisca
/// che quella lista resti completa: se domani una nuova tabella acquisisce una colonna ConversationId
/// e nessuno la aggiunge al purger, le sue righe SOPRAVVIVONO alla cancellazione: dati orfani che
/// vanificano la garanzia di crypto-shred (la conversazione "cancellata" lascia tracce in chiaro su
/// quella tabella).
///
/// Questo test NON hard-coda la lista delle tabelle: riflette sul modello EF
/// (<c>context.Model.GetEntityTypes()</c>) per trovare OGNI entità con una proprietà
/// <c>ConversationId</c>, semina una riga per ciascuna sotto un unico conversationId, esegue la purga
/// e pretende zero righe residue ovunque. Aggiungere una tabella conversation-scoped al modello senza
/// aggiungerla al purger fa fallire questo test in modo rumoroso, senza bisogno di toccare il test.
///
/// Esclusione intenzionale: <see cref="AuditLogEntity"/>. L'audit log è append-only by design (la
/// riga di cancellazione stessa vi viene aggiunta), quindi NON deve essere cancellato dalla purga.
/// </summary>
public class ConversationPurgerCompletenessTests : IDisposable
{
    private readonly EchatDbContext _db;

    public ConversationPurgerCompletenessTests()
    {
        var options = new DbContextOptionsBuilder<EchatDbContext>()
            .UseInMemoryDatabase($"echat-{Guid.NewGuid()}")
            .Options;
        _db = new EchatDbContext(options);
    }

    public void Dispose() => _db.Dispose();

    /// <summary>
    /// L'audit log è append-only ed è esplicitamente escluso dal crypto-shred. Ogni altra entità
    /// con una colonna ConversationId DEVE essere cancellata dalla purga.
    /// </summary>
    private static readonly HashSet<Type> ExcludedFromShred = new() { typeof(AuditLogEntity) };

    [Fact]
    public async Task Purge_DeletesEveryConversationScopedTable_DiscoveredViaModelReflection()
    {
        var conversationId = Guid.NewGuid();

        // (1) Scopri dal modello EF ogni entità con una proprietà ConversationId (escluso l'audit log).
        var scopedTypes = _db.Model.GetEntityTypes()
            .Select(t => t.ClrType)
            .Where(t => ConversationIdProperty(t) is not null)
            .Where(t => !ExcludedFromShred.Contains(t))
            .Distinct()
            .ToList();

        // Sanity: se la riflessione non trova nulla, il test passerebbe a vuoto senza provare niente.
        scopedTypes.Should().NotBeEmpty(
            "il modello deve avere tabelle conversation-scoped; lista vuota = test inutile");

        // (2) Semina una riga per tabella sotto lo stesso conversationId.
        foreach (var clrType in scopedTypes)
        {
            var entity = Activator.CreateInstance(clrType)!;
            ConversationIdProperty(clrType)!.SetValue(entity, conversationId);
            _db.Add(entity);
        }
        await _db.SaveChangesAsync();

        // Verifica che il seeding sia andato a buon fine: ogni tabella ha la sua riga.
        foreach (var clrType in scopedTypes)
        {
            CountForConversation(clrType, conversationId).Should().Be(1,
                "il seeding deve aver inserito una riga in {0}", clrType.Name);
        }

        // (3) Esegui il crypto-shred.
        var purger = new ConversationPurger(_db, new NoopBlobStorage());
        await purger.PurgeAsync(conversationId, new AuditEntry
        {
            ConversationId = conversationId,
            UserId = Guid.NewGuid(),
            Action = "ConversationDeleted",
            Timestamp = DateTime.UtcNow,
            Details = null
        });

        // (4) Ogni tabella conversation-scoped deve essere a zero righe per questo conversationId.
        // Se una nuova tabella è stata aggiunta al modello ma NON al purger, qui fallisce.
        foreach (var clrType in scopedTypes)
        {
            CountForConversation(clrType, conversationId).Should().Be(0,
                "ConversationPurger deve cancellare tutte le righe di {0} per il conversationId: " +
                "se questa tabella è nuova, aggiungila a ConversationPurger.PurgeAsync " +
                "(o, se è append-only by design, escludila in ExcludedFromShred)", clrType.Name);
        }
    }

    private static System.Reflection.PropertyInfo? ConversationIdProperty(Type clrType)
    {
        var prop = clrType.GetProperty("ConversationId");
        return prop is not null && prop.PropertyType == typeof(Guid) ? prop : null;
    }

    /// <summary>
    /// Conta le righe di <paramref name="clrType"/> il cui ConversationId combacia, interrogando il
    /// DbSet via <c>Set&lt;T&gt;()</c> risolto a runtime (il modello è scoperto per riflessione,
    /// quindi non possiamo usare i DbSet tipizzati direttamente).
    /// </summary>
    private int CountForConversation(Type clrType, Guid conversationId)
    {
        var setMethod = typeof(DbContext).GetMethods()
            .Single(m => m.Name == nameof(DbContext.Set)
                         && m.IsGenericMethodDefinition
                         && m.GetParameters().Length == 0)
            .MakeGenericMethod(clrType);
        var dbSet = (IEnumerable)setMethod.Invoke(_db, null)!;

        var prop = ConversationIdProperty(clrType)!;
        return dbSet.Cast<object>().Count(row => (Guid)prop.GetValue(row)! == conversationId);
    }

    /// <summary>
    /// Fake no-op di <see cref="IBlobStorageService"/>: la purga chiama solo DeleteConversationAsync
    /// e qui non c'è storage reale da toccare.
    /// </summary>
    private sealed class NoopBlobStorage : IBlobStorageService
    {
        public Task<FileUploadSession> BeginUploadAsync(Guid conversationId, Guid ownerUserId)
            => Task.FromResult(new FileUploadSession());
        public Task StorePartAsync(Guid conversationId, Guid userId, Guid fileId, string uploadToken, int partNo, byte[] encryptedBytes)
            => Task.CompletedTask;
        public Task<FileCommitResult> FinalizeAsync(Guid conversationId, Guid userId, Guid fileId, string uploadToken)
            => Task.FromResult(new FileCommitResult());
        public Task<Stream> ReadAsync(Guid conversationId, Guid fileId)
            => Task.FromResult<Stream>(new MemoryStream());
        public Task DeleteConversationAsync(Guid conversationId, IReadOnlyCollection<Guid> fileIds)
            => Task.CompletedTask;
    }
}
