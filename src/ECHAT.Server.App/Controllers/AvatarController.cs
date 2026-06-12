using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using ECHAT.Server.App.Data;
using ECHAT.Server.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace ECHAT.Server.App.Controllers;

// Endpoint pubblico per scelta: gli avatar sono caricati via tag <img src="api/avatar/{id}">,
// e il browser non può allegare il bearer JWT a una richiesta <img>. Richiedere [Authorize] qui
// romperebbe il rendering degli avatar in tutta la UI. L'accesso è a basso rischio (immagine di
// profilo già pubblica su Google, indirizzata da un GUID opaco non enumerabile). [AllowAnonymous]
// rende esplicita la scelta così non viene scambiata per una svista in un audit.
[AllowAnonymous]
[ApiController]
[Route("api/[controller]")]
public class AvatarController : ControllerBase
{
    private readonly EchatDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly AvatarService _avatars;

    public AvatarController(EchatDbContext db, IMemoryCache cache, AvatarService avatars)
    {
        _db = db;
        _cache = cache;
        _avatars = avatars;
    }

    /// <summary>
    /// Fa da proxy e cache per la foto profilo Google dell'utente.
    /// Il browser può tenere in cache la risposta per 24h tramite l'header Cache-Control.
    /// </summary>
    [HttpGet("{userId}")]
    [ResponseCache(Duration = 86400, Location = ResponseCacheLocation.Any)]
    public async Task<IActionResult> Get(Guid userId)
    {
        var cacheKey = $"avatar_{userId}";

        if (_cache.TryGetValue(cacheKey, out AvatarData? cached) && cached != null)
        {
            return File(cached.Data, cached.ContentType);
        }

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user == null || string.IsNullOrEmpty(user.PictureUrl))
            return NotFound();

        // Client dedicato anti-SSRF: niente redirect automatici (un 30x verso un indirizzo interno
        // non deve bypassare la validazione su PictureUrl). In più un ConnectCallback ri-valida l'IP
        // *al momento del connect*: chiude la finestra DNS-rebinding (TOCTOU) fra la risoluzione fatta
        // in AvatarService e la connessione effettiva: se il nome (anche se in allow-list) risolve a
        // un IP privato/riservato, il connect viene rifiutato. AvatarService applica già https,
        // allow-list host, timeout e cap dimensione. Costo accettabile: cache 24h.
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectCallback = async (ctx, ct) =>
            {
                var entries = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct);
                var ip = Array.Find(entries, a => !AvatarService.IsPrivateOrReserved(a))
                    ?? throw new HttpRequestException("Avatar host resolves only to private/reserved addresses.");
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(ip, ctx.DnsEndPoint.Port), ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
        using var client = new HttpClient(handler);
        var avatar = await _avatars.FetchFromExternalUrlAsync(client, user.PictureUrl);
        if (avatar == null)
            return NotFound();

        _cache.Set(cacheKey, avatar, TimeSpan.FromHours(24));

        return File(avatar.Data, avatar.ContentType);
    }
}
