using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace ECHAT.Server.Core.Services;

/// <summary>
/// Avatar scaricato: byte dell'immagine + content-type risolto (default <c>image/jpeg</c>).
/// </summary>
public class AvatarData
{
    public byte[] Data { get; init; } = Array.Empty<byte>();
    public string ContentType { get; init; } = "image/jpeg";
}

/// <summary>
/// Recupera la foto profilo da un URL esterno con difese anti-SSRF: schema https obbligatorio,
/// allow-list degli host CDN di Google (la PictureUrl proviene dalla claim <c>picture</c> dell'OAuth
/// Google), rifiuto di IP privati/loopback/link-local, niente redirect automatici, timeout breve e
/// cap sulla dimensione della risposta. La App fornisce l'<see cref="HttpClient"/> (via
/// IHttpClientFactory, configurato senza redirect automatici), risolve la PictureUrl dell'utente,
/// gestisce la cache (IMemoryCache + TTL 24h) e il mapping 404/200.
/// </summary>
public class AvatarService
{
    /// <summary>Dimensione massima accettata per l'immagine scaricata (5 MB).</summary>
    public const int MaxResponseBytes = 5 * 1024 * 1024;

    /// <summary>Timeout massimo per la fetch esterna.</summary>
    public static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Scarica l'avatar da <paramref name="pictureUrl"/>. Ritorna <c>null</c> se la URL è vuota,
    /// non valida (vedi <see cref="IsAllowedUrl"/>), se la risposta non è di successo, supera il
    /// limite di dimensione o se la richiesta lancia un'eccezione.
    /// </summary>
    public async Task<AvatarData?> FetchFromExternalUrlAsync(HttpClient client, string? pictureUrl)
    {
        if (!IsAllowedUrl(pictureUrl, out var uri) || uri is null)
            return null;

        try
        {
            using var cts = new CancellationTokenSource(FetchTimeout);

            // Difesa in profondità anti-SSRF/DNS-rebind: l'host è già in allow-list, ma risolviamo
            // comunque il nome e rifiutiamo se uno qualunque degli IP è privato/riservato. Una
            // protezione completa contro il rebinding TOCTOU richiederebbe un ConnectCallback che
            // ri-valida l'IP al momento del connect (configurabile sull'HttpClient lato App); qui
            // chiudiamo almeno il caso di un host allow-listed che risolve a un indirizzo interno.
            IPAddress[] addresses;
            try { addresses = await Dns.GetHostAddressesAsync(uri.Host, cts.Token); }
            catch { return null; }
            if (addresses.Length == 0 || addresses.Any(IsPrivateOrReserved))
                return null;

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);

            // ResponseHeadersRead: non bufferizziamo il corpo finché non abbiamo validato l'esito,
            // così possiamo applicare il cap di dimensione senza scaricare tutto in memoria.
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            // Niente redirect automatici (client configurato di conseguenza). Se l'upstream tenta un
            // redirect lo trattiamo come fallimento: non seguiamo verso destinazioni non validate.
            if (!response.IsSuccessStatusCode)
                return null;

            var declaredLength = response.Content.Headers.ContentLength;
            if (declaredLength.HasValue && declaredLength.Value > MaxResponseBytes)
                return null;

            var data = await ReadCappedAsync(response, cts.Token);
            if (data is null)
                return null;

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
            return new AvatarData { Data = data, ContentType = contentType };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Legge il corpo della risposta applicando il cap di <see cref="MaxResponseBytes"/>.
    /// Ritorna <c>null</c> se il flusso supera il limite.
    /// </summary>
    private static async Task<byte[]?> ReadCappedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), ct)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// Valida l'URL dell'avatar (logica pura, testabile). Regole:
    /// schema esclusivamente <c>https</c>; host nell'allow-list di Google
    /// (<c>googleusercontent.com</c> e sottodomini, <c>www.google.com</c>); rifiuto di host che sono
    /// indirizzi IP privati/loopback/link-local/unique-local. Qualsiasi altra cosa è "not allowed".
    /// </summary>
    public static bool IsAllowedUrl(string? url, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
            return false;

        if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;

        var host = parsed.Host;
        if (string.IsNullOrEmpty(host))
            return false;

        // Host come IP letterale: mai in allow-list (la CDN Google è raggiunta per nome host).
        // Lo rifiutiamo sempre, a maggior ragione se è un intervallo privato/riservato.
        if (IPAddress.TryParse(host, out _))
            return false;

        // Allow-list: la PictureUrl arriva dalla claim Google "picture", servita dalla CDN Google.
        if (!IsAllowedHost(host))
            return false;

        uri = parsed;
        return true;
    }

    /// <summary>
    /// Verifica che l'host appartenga ai domini Google attesi per le foto profilo.
    /// </summary>
    public static bool IsAllowedHost(string host)
    {
        host = host.TrimEnd('.').ToLowerInvariant();
        return host == "googleusercontent.com"
            || host.EndsWith(".googleusercontent.com", StringComparison.Ordinal)
            || host == "google.com"
            || host.EndsWith(".google.com", StringComparison.Ordinal);
    }

    /// <summary>
    /// Vero se l'indirizzo IP appartiene a un intervallo privato/loopback/link-local/unique-local o
    /// comunque non instradabile pubblicamente.
    /// </summary>
    public static bool IsPrivateOrReserved(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
            return true;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            // 10.0.0.0/8
            if (b[0] == 10) return true;
            // 172.16.0.0/12
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            // 192.168.0.0/16
            if (b[0] == 192 && b[1] == 168) return true;
            // 127.0.0.0/8 (loopback, ridondante con IsLoopback ma esplicito)
            if (b[0] == 127) return true;
            // 169.254.0.0/16 (link-local)
            if (b[0] == 169 && b[1] == 254) return true;
            // 100.64.0.0/10 (CGNAT)
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;
            // 0.0.0.0/8
            if (b[0] == 0) return true;
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
                return true;

            // ::1 già coperto da IsLoopback. Mappatura IPv4-in-IPv6 -> valuta come IPv4.
            if (address.IsIPv4MappedToIPv6)
                return IsPrivateOrReserved(address.MapToIPv4());

            var b = address.GetAddressBytes();
            // fc00::/7 (unique local)
            if ((b[0] & 0xFE) == 0xFC) return true;
            // fe80::/10 (link-local, ridondante con IsIPv6LinkLocal)
            if (b[0] == 0xFE && (b[1] & 0xC0) == 0x80) return true;
            return false;
        }

        // Famiglia di indirizzi sconosciuta: tratta come non sicura.
        return true;
    }
}
