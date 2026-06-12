using ECHAT.Models.Dtos;

namespace ECHAT.Server.Core.Interfaces;

public interface IAuditLog
{
    Task RecordAsync(AuditEntry entry);

    /// <summary>Rimuove le voci con timestamp anteriore a <paramref name="cutoff"/>. Ritorna il numero rimosso.</summary>
    Task<int> PurgeOlderThanAsync(DateTime cutoff);

    /// <summary>
    /// Lettura paginata per la console admin. Ordinamento per Timestamp desc, applica i
    /// filtri opzionali e clampa Limit al massimo lato repository per evitare DoS.
    /// </summary>
    Task<List<AuditEntry>> QueryAsync(AuditQueryFilter filter);
}
