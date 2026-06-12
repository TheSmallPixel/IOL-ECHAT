using System.Collections.Concurrent;
using ECHAT.Models.Dtos;
using ECHAT.Models.Events;
using ECHAT.Server.Core.Exceptions;
using ECHAT.Server.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace ECHAT.Server.Core.Services;

/// <summary>
/// Operazioni sulle conversazioni: creazione, lista, dettagli, membri, ownership transfer.
/// Concentra qui la logica di policy + key distribution + audit + notify, così il controller
/// resta sottile (claim parsing + autorizzazione + mappatura errori HTTP).
/// </summary>
public class ConversationOperationsService
{
    private readonly IConversationStore _conversations;
    private readonly IMemberStore _members;
    private readonly IUserStore _users;
    private readonly ISeqCounterStore _seqCounters;
    private readonly IAuditLog _audit;
    private readonly IRealtimeNotifier _notifier;
    private readonly IConversationPurger _purger;
    private readonly IKeyEnvelopeStore _keyStore;
    private readonly IDevicePublicKeyStore _devices;
    private readonly ILogger<ConversationOperationsService> _logger;

    /// <summary>Lunghezza massima del nome di una conversazione.</summary>
    public const int MaxNameLength = 100;

    // C2: lock per-conversazione che serializza le mutazioni di membership/ruolo (add/remove/role/
    // transfer). Senza, due operazioni Owner concorrenti possono interlacciarsi e lasciare la
    // conversazione orfana (zero Owner): es. un TransferOwnership che promuove X a Owner mentre un
    // ChangeRole(X,"Member") concorrente lo declassa. Serializzandole, il read-check-write diventa
    // atomico. Vale per il singolo nodo (stesso pattern di SequenceService.ReserveLocks); per il
    // multi-instance servirebbe un lock distribuito; stesso follow-up di sequenza/quota.
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> MembershipLocks = new();

    private static async Task<T> WithMembershipLockAsync<T>(Guid conversationId, Func<Task<T>> action)
    {
        var sem = MembershipLocks.GetOrAdd(conversationId, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync();
        try { return await action(); }
        finally { sem.Release(); }
    }

    private static Task WithMembershipLockAsync(Guid conversationId, Func<Task> action)
        => WithMembershipLockAsync<bool>(conversationId, async () => { await action(); return true; });

    public ConversationOperationsService(
        IConversationStore conversations,
        IMemberStore members,
        IUserStore users,
        ISeqCounterStore seqCounters,
        IAuditLog audit,
        IRealtimeNotifier notifier,
        IConversationPurger purger,
        IKeyEnvelopeStore keyStore,
        IDevicePublicKeyStore devices,
        ILogger<ConversationOperationsService> logger)
    {
        _conversations = conversations;
        _members = members;
        _users = users;
        _seqCounters = seqCounters;
        _audit = audit;
        _notifier = notifier;
        _purger = purger;
        _keyStore = keyStore;
        _devices = devices;
        _logger = logger;
    }

    public Task<List<ConversationSummary>> ListForUserAsync(Guid userId)
        => _conversations.ListForUserAsync(userId);

    // Autorizzazione (membership attiva) applicata a monte dal filtro
    // [RequireConversationPermission(Permission.Read)] sul controller.
    public async Task<ConversationRecord> GetAsync(Guid conversationId)
    {
        return await _conversations.GetAsync(conversationId)
            ?? throw new NotFoundException($"Conversation {conversationId} not found");
    }

    // Autorizzazione (membership attiva) applicata a monte dal filtro
    // [RequireConversationPermission(Permission.Read)] sul controller.
    public Task<List<MemberWithUser>> ListMembersAsync(Guid conversationId)
        => _members.ListActiveWithUserAsync(conversationId);

    public async Task<ConversationRecord> CreateAsync(string? requestedName, Guid creatorUserId)
    {
        var conversationId = Guid.NewGuid();
        // D7: normalizziamo/tronchiamo come RenameAsync, così create e rename sono coerenti
        // (niente nomi oltre MaxNameLength persistiti dal path di creazione).
        var name = (requestedName ?? string.Empty).Trim();
        if (name.Length == 0) name = "New Conversation";
        if (name.Length > MaxNameLength) name = name[..MaxNameLength];
        var record = new ConversationRecord
        {
            Id = conversationId,
            Name = name,
            CurrentEpochId = 1,
            CreatedAt = DateTime.UtcNow,
            CreatedByUserId = creatorUserId
        };

        await _conversations.CreateAsync(record);
        await _members.AddAsync(conversationId, creatorUserId, role: "Owner");

        // Inizializza counter seq + anchor così il primo lease parte da 1 senza self-heal.
        await _seqCounters.UpdateAnchorAsync(conversationId, anchorSeq: 0, anchorEnvelopeHash: Array.Empty<byte>());

        // E2EE (S1): NESSUNA CEK generata server-side. Il client del creatore provisiona l'epoch 1
        // (genera la CEK, la wrappa con la propria chiave pubblica RSA e la posta via POST /keys).
        // La conversazione resta senza chiavi finché il client non provisiona.

        await _audit.RecordAsync(new AuditEntry
        {
            ConversationId = conversationId,
            UserId = creatorUserId,
            Action = "ConversationCreated",
            Timestamp = DateTime.UtcNow,
            Details = $"name={record.Name}"
        });

        _logger.LogInformation(
            "Conversation created: conversationId={ConversationId} creatorId={CreatorId} epochId={EpochId}",
            conversationId, creatorUserId, record.CurrentEpochId);

        return record;
    }

    public async Task AddMemberAsync(
        Guid conversationId, Guid requesterId, Guid targetUserId, bool includeHistory)
    {
        // Autorizzazione (Owner/Admin) applicata a monte dal filtro
        // [RequireConversationPermission(Permission.AddMember)] sul controller.
        if (await _members.GetActiveAsync(conversationId, targetUserId) is not null)
            throw new ConflictException("User is already a member.");

        if (!await _users.ExistsAsync(targetUserId))
            throw new NotFoundException("User not found.");

        await _members.AddAsync(conversationId, targetUserId, role: "Member");

        _ = await _conversations.GetAsync(conversationId)
            ?? throw new NotFoundException($"Conversation {conversationId} not found");

        // E2EE (S1): il server NON copia più i wrap (impossibile: ogni wrap è cifrato per un device
        // specifico). Il client dell'admin che esegue l'add ri-wrappa la CEK con la chiave pubblica
        // RSA del/i device del nuovo membro e la posta via POST /keys (includeHistory = anche le CEK
        // degli epoch passati). includeHistory è propagato al client via l'evento/flow, non qui.

        await _audit.RecordAsync(new AuditEntry
        {
            ConversationId = conversationId,
            UserId = requesterId,
            Action = "MemberAdded",
            Timestamp = DateTime.UtcNow,
            Details = $"target={targetUserId}"
        });

        await _notifier.NotifyAsync(conversationId, new MemberChangedEvent
        {
            ConversationId = conversationId,
            UserId = targetUserId,
            Action = "Added",
            Timestamp = DateTime.UtcNow
        });

        _logger.LogInformation(
            "Member added: conversationId={ConversationId} targetUserId={TargetUserId} requesterId={RequesterId} includeHistory={IncludeHistory}",
            conversationId, targetUserId, requesterId, includeHistory);
    }

    public Task<int> RemoveMemberAsync(Guid conversationId, Guid requesterId, Guid targetUserId)
        => WithMembershipLockAsync(conversationId, async () =>
    {
        // Autorizzazione del richiedente (Owner/Admin) applicata a monte dal filtro
        // [RequireConversationPermission(Permission.RemoveMember)] sul controller.
        //
        // Quella che segue NON è un controllo sul ruolo del richiedente ma una regola di business
        // sul *bersaglio*: l'Owner non si rimuove direttamente (lascerebbe la conversazione orfana).
        // Il MembershipLock serializza questa op con ChangeRole/TransferOwnership: nessun
        // TransferOwnership concorrente può promuovere il bersaglio a Owner fra il check e la
        // SoftRemove, quindi il controllo sul ruolo è atomico (single-instance).
        var target = await _members.GetActiveAsync(conversationId, targetUserId)
            ?? throw new NotFoundException("Membership not found.");
        if (target.Role == "Owner")
            throw new ForbiddenException();

        var removed = await _members.SoftRemoveAsync(conversationId, targetUserId);
        if (!removed)
            throw new NotFoundException("Membership not found.");

        // Rotation dell'epoch: i messaggi futuri usano un epoch di cui il membro rimosso non ha le key.
        // E2EE (S1): il server bumpa solo l'epoch; NON genera la nuova CEK. Il client dell'admin genera
        // la CEK del nuovo epoch e la wrappa per i device dei membri rimasti (POST /keys). Il membro
        // rimosso non riceve wrap per il nuovo epoch  non può leggere i messaggi futuri.
        var newEpoch = await _conversations.IncrementEpochAsync(conversationId);
        var remaining = (await _members.ListActiveWithUserAsync(conversationId))
            .Select(m => m.UserId).ToList();

        // Crypto-shred dei wrap del rimosso: KeyAccessService già nega le key ai non-membri,
        // ma cancellare le sue copie wrappate (tutti gli epoch, tutti i suoi device) toglie
        // anche la possibilità teorica di recuperarle da un dump del DB con la sua privata.
        var targetDevices = (await _devices.GetActiveForUserAsync(targetUserId))
            .Select(d => d.DeviceId).ToList();
        await _keyStore.DeleteWrapsForDevicesAsync(conversationId, targetDevices);

        await _audit.RecordAsync(new AuditEntry
        {
            ConversationId = conversationId,
            UserId = requesterId,
            Action = "MemberRemoved",
            Timestamp = DateTime.UtcNow,
            Details = $"target={targetUserId};newEpoch={newEpoch}"
        });

        // L'utente appena rimosso non è più fra i membri attivi, quindi il broadcast standard
        // `NotifyAsync(conversationId, ...)` lo escluderebbe, ma vogliamo proprio che la sua UI
        // sappia di essere stata rimossa per togliere la conversazione dalla sidebar. Includiamolo
        // esplicitamente nell'audience del MemberChanged.
        var notifyRecipients = remaining.Append(targetUserId);
        await _notifier.NotifyUsersAsync(notifyRecipients, new MemberChangedEvent
        {
            ConversationId = conversationId,
            UserId = targetUserId,
            Action = "Removed",
            Timestamp = DateTime.UtcNow
        });
        await _notifier.NotifyAsync(conversationId, new EpochRotatedEvent
        {
            ConversationId = conversationId,
            NewEpochId = newEpoch,
            Timestamp = DateTime.UtcNow
        });

        _logger.LogInformation(
            "Member removed and epoch rotated: conversationId={ConversationId} targetUserId={TargetUserId} requesterId={RequesterId} newEpochId={NewEpochId} remainingMembers={RemainingCount}",
            conversationId, targetUserId, requesterId, newEpoch, remaining.Count);

        return newEpoch;
    });

    public Task TransferOwnershipAsync(Guid conversationId, Guid requesterId, Guid targetUserId)
        => WithMembershipLockAsync(conversationId, async () =>
    {
        // Autorizzazione (solo Owner) applicata a monte dal filtro
        // [RequireConversationPermission(Permission.TransferOwnership)] sul controller.
        // Serializzato col MembershipLock: lo swap dei due ruoli è atomico rispetto a
        // ChangeRole/RemoveMember concorrenti (single-instance).
        if (await _members.GetActiveAsync(conversationId, targetUserId) is null)
            throw new NotFoundException("Target user is not a member.");

        await _members.SetRoleAsync(conversationId, requesterId, "Admin");
        await _members.SetRoleAsync(conversationId, targetUserId, "Owner");

        await _audit.RecordAsync(new AuditEntry
        {
            ConversationId = conversationId,
            UserId = requesterId,
            Action = "OwnershipTransferred",
            Timestamp = DateTime.UtcNow,
            Details = $"target={targetUserId}"
        });

        // Notifichiamo i membri attivi che i ruoli sono cambiati. Senza questo, il browser del nuovo
        // owner continuerebbe a vedere il vecchio pannello (e quindi il vecchio owner, ora Admin,
        // potrebbe avere ancora il bottone Remove sul nuovo owner finché non ricarica la pagina).
        // Action="RoleChanged": il client lo tratta come "ricarica i membri di questa conversazione".
        var ts = DateTime.UtcNow;
        await _notifier.NotifyAsync(conversationId, new MemberChangedEvent
        {
            ConversationId = conversationId,
            UserId = targetUserId,
            Action = "RoleChanged",
            Timestamp = ts
        });
        await _notifier.NotifyAsync(conversationId, new MemberChangedEvent
        {
            ConversationId = conversationId,
            UserId = requesterId,
            Action = "RoleChanged",
            Timestamp = ts
        });

        _logger.LogInformation(
            "Ownership transferred: conversationId={ConversationId} fromUserId={FromUserId} toUserId={ToUserId}",
            conversationId, requesterId, targetUserId);
    });

    /// <summary>
    /// Cambia il ruolo di un membro fra "Admin" e "Member". Autorizzazione (solo Owner) applicata
    /// a monte dal filtro <c>[RequireConversationPermission(Permission.ManageRoles)]</c>. Il ruolo
    /// "Owner" non si tocca da qui: per cambiare proprietario si usa TransferOwnership.
    /// </summary>
    public Task ChangeRoleAsync(Guid conversationId, Guid requesterId, Guid targetUserId, string newRole)
        => WithMembershipLockAsync(conversationId, async () =>
    {
        if (newRole is not ("Admin" or "Moderator" or "Member"))
            throw new ValidationException("Role must be 'Admin', 'Moderator' or 'Member'.");

        // Serializzato col MembershipLock: nessun TransferOwnership concorrente può promuovere il
        // bersaglio a Owner fra questo check e la SetRole, quindi non si rischia di declassare il
        // nuovo Owner lasciando la conversazione orfana (single-instance).
        var target = await _members.GetActiveAsync(conversationId, targetUserId)
            ?? throw new NotFoundException("Membership not found.");
        if (target.Role == "Owner")
            throw new ForbiddenException();
        if (target.Role == newRole)
            return; // no-op idempotente

        await _members.SetRoleAsync(conversationId, targetUserId, newRole);

        await _audit.RecordAsync(new AuditEntry
        {
            ConversationId = conversationId,
            UserId = requesterId,
            Action = "RoleChanged",
            Timestamp = DateTime.UtcNow,
            Details = $"target={targetUserId};newRole={newRole}"
        });

        // I client trattano MemberChanged/RoleChanged come "ricarica i membri di questa conversazione",
        // così i pannelli admin riflettono subito il nuovo ruolo (bottoni Remove/Manage compresi).
        await _notifier.NotifyAsync(conversationId, new MemberChangedEvent
        {
            ConversationId = conversationId,
            UserId = targetUserId,
            Action = "RoleChanged",
            Timestamp = DateTime.UtcNow
        });

        _logger.LogInformation(
            "Role changed: conversationId={ConversationId} targetUserId={TargetUserId} newRole={NewRole} requesterId={RequesterId}",
            conversationId, targetUserId, newRole, requesterId);
    });

    /// <summary>
    /// Rinomina la conversazione. Autorizzazione (Owner/Admin) applicata a monte dal filtro
    /// <c>[RequireConversationPermission(Permission.Admin)]</c>. Il nome viene normalizzato (trim) e
    /// troncato a <see cref="MaxNameLength"/>; il controller rifiuta a monte un nome vuoto.
    /// </summary>
    public async Task RenameAsync(Guid conversationId, Guid requesterId, string newName)
    {
        var trimmed = (newName ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            throw new ValidationException("Conversation name cannot be empty.");
        // C5: tronchiamo lato server. Audit e notify più sotto usano già `trimmed` (il valore
        // effettivamente persistito), quindi i client ricevono il nome canonico e si riallineano.
        if (trimmed.Length > MaxNameLength)
            trimmed = trimmed[..MaxNameLength];

        _ = await _conversations.GetAsync(conversationId)
            ?? throw new NotFoundException($"Conversation {conversationId} not found");

        await _conversations.RenameAsync(conversationId, trimmed);

        await _audit.RecordAsync(new AuditEntry
        {
            ConversationId = conversationId,
            UserId = requesterId,
            Action = "ConversationRenamed",
            Timestamp = DateTime.UtcNow,
            Details = $"name={trimmed}"
        });

        await _notifier.NotifyAsync(conversationId, new ConversationChangedEvent
        {
            ConversationId = conversationId,
            ChangeType = "Renamed",
            Name = trimmed,
            Timestamp = DateTime.UtcNow
        });

        _logger.LogInformation(
            "Conversation renamed: conversationId={ConversationId} requesterId={RequesterId}",
            conversationId, requesterId);
    }

    /// <summary>
    /// Cancella definitivamente la conversazione (crypto-shred): rimuove messaggi, membership, key
    /// envelope, contatore di sequenza, chain boundary, file e blob cifrati. Autorizzazione (solo
    /// Owner) applicata a monte dal filtro <c>[RequireConversationPermission(Permission.DeleteConversation)]</c>.
    /// Il log di audit (append-only) viene preservato; la riga di cancellazione viene scritta dal
    /// purger nella stessa transazione atomica della purga. I destinatari da notificare vengono
    /// *catturati* prima della purga (dopo non ci sarebbero più membri da risolvere), ma la notifica
    /// vera e propria parte *dopo* che la purga è andata a buon fine, così le UI tolgono la
    /// conversazione dalla sidebar solo se la cancellazione è effettivamente avvenuta.
    /// </summary>
    public async Task DeleteAsync(Guid conversationId, Guid requesterId)
    {
        _ = await _conversations.GetAsync(conversationId)
            ?? throw new NotFoundException($"Conversation {conversationId} not found");

        // Catturiamo i destinatari PRIMA della purga: dopo non ci sono più membri da risolvere.
        var recipients = (await _members.ListActiveWithUserAsync(conversationId))
            .Select(m => m.UserId).ToList();

        // L'audit di cancellazione viene scritto dal purger nella stessa transazione della purga
        // (atomicità: o cancelliamo tutto + audit, o niente).
        var deletionAudit = new AuditEntry
        {
            ConversationId = conversationId,
            UserId = requesterId,
            Action = "ConversationDeleted",
            Timestamp = DateTime.UtcNow,
            Details = $"members={recipients.Count}"
        };

        await _purger.PurgeAsync(conversationId, deletionAudit);

        await _notifier.NotifyUsersAsync(recipients, new ConversationChangedEvent
        {
            ConversationId = conversationId,
            ChangeType = "Deleted",
            Timestamp = DateTime.UtcNow
        });

        _logger.LogInformation(
            "Conversation deleted (crypto-shred): conversationId={ConversationId} requesterId={RequesterId} notifiedMembers={Count}",
            conversationId, requesterId, recipients.Count);
    }
}
