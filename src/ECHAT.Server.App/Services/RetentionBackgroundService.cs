using ECHAT.Server.Core.Interfaces;
using ECHAT.Server.Core.Services;

namespace ECHAT.Server.App.Services;

/// <summary>
/// Ripulisce periodicamente i lease scaduti e le righe vecchie dell'audit log.
/// Cadenza di default oraria; finestra di retention dell'audit log 90 giorni. Entrambi configurabili.
/// </summary>
public class RetentionBackgroundService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<RetentionBackgroundService> _logger;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _auditMaxAge;

    public RetentionBackgroundService(
        IServiceProvider services,
        ILogger<RetentionBackgroundService> logger,
        IConfiguration config)
    {
        _services = services;
        _logger = logger;

        var section = config.GetSection("Retention");
        var hours = section.GetValue<double?>("SweepIntervalHours") ?? 1.0;
        var auditDays = section.GetValue<double?>("AuditLogMaxAgeDays") ?? 90.0;
        _interval = TimeSpan.FromHours(Math.Max(0.05, hours));
        _auditMaxAge = TimeSpan.FromDays(Math.Max(1, auditDays));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Prima passata all'avvio, poi alla cadenza configurata.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _services.CreateScope();
                var leases = scope.ServiceProvider.GetRequiredService<ISeqLeaseStore>();
                var audit = scope.ServiceProvider.GetRequiredService<IAuditLog>();
                var retention = new RetentionService(leases, audit, _auditMaxAge);
                var now = DateTime.UtcNow;

                var leasesPurged = await retention.PurgeExpiredLeasesAsync(now);
                var auditsPurged = await retention.PurgeOldAuditLogAsync(now);

                if (leasesPurged > 0 || auditsPurged > 0)
                {
                    _logger.LogInformation(
                        "Retention sweep: leasesPurged={Leases} auditPurged={Audits}",
                        leasesPurged, auditsPurged);
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Retention sweep failed; will retry on next interval");
            }

            try { await Task.Delay(_interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
