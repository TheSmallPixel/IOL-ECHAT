using ECHAT.Server.Core.Interfaces;

namespace ECHAT.Server.Core.Services;

/// <summary>
/// Logica di retention: cancella i lease seq scaduti e l'audit log più vecchio della finestra
/// configurata. Indipendente dal framework: l'<c>IHostedService</c> sta in App.
/// </summary>
public class RetentionService
{
    private readonly ISeqLeaseStore _leases;
    private readonly IAuditLog _audit;

    public TimeSpan AuditLogMaxAge { get; }

    public RetentionService(ISeqLeaseStore leases, IAuditLog audit, TimeSpan? auditLogMaxAge = null)
    {
        _leases = leases;
        _audit = audit;
        AuditLogMaxAge = auditLogMaxAge ?? TimeSpan.FromDays(90);
    }

    public Task<int> PurgeExpiredLeasesAsync(DateTime now)
        => _leases.PurgeExpiredAsync(now);

    public Task<int> PurgeOldAuditLogAsync(DateTime now)
        => _audit.PurgeOlderThanAsync(now - AuditLogMaxAge);
}
