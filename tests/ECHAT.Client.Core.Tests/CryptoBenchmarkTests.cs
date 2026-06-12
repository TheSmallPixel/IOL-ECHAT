using System.Diagnostics;
using System.Security.Cryptography;
using FluentAssertions;
using Xunit.Abstractions;

namespace ECHAT.Client.Core.Tests;

/// <summary>
/// Verifica la performance dichiarata nella doc (intro.md sez. 1.3 Performance,
/// ChatSdkService.SendFileAsync): AES-GCM in hardware è molto più veloce del cipher
/// C# HMAC-CTR. I file ora passano sempre dal worker JS AES-GCM; HMAC-CTR resta in uso
/// per il message engine (<c>HmacCtrCryptoEngine</c>) e per il wrap della DEK, quindi il
/// benchmark misura ancora il costo del path C#.
///
/// Non possiamo pilotare un Web Worker da xUnit, quindi misuriamo gli **analoghi lato CLR**:
///
///   - <see cref="HmacCtrEncryptAsync"/>: copia fedele di
///     <c>SimpleCryptoSuiteFactory.HmacCtrCipher.EncryptAsync</c>, il loop HMAC-CTR a chunk
///     usato dal message engine.
///   - <see cref="AesGcmEncrypt"/>: <see cref="System.Security.Cryptography.AesGcm"/>,
///     che usa AES-NI sulla CPU come fa <c>crypto.subtle.encrypt(AES-GCM)</c> dentro
///     <c>crypto-worker.js</c>.
///
/// Il rapporto sul CLR è un **lower bound conservativo** rispetto al browser: WASM è
/// 3-10x più lento del CLR sul codice software puro (HMAC-CTR), mentre AES-NI gira a
/// velocità hardware in entrambi i runtime. Il divario quindi cresce nel browser.
///
/// I numeri vengono stampati nell'output del test (usa
/// `dotnet test -l "console;verbosity=detailed"` o VS Test Explorer), così possono
/// rimpiazzare il "tipicamente" della doc con misure reali sulla macchina.
/// </summary>
public class CryptoBenchmarkTests
{
    private readonly ITestOutputHelper _out;
    public CryptoBenchmarkTests(ITestOutputHelper output) => _out = output;

    /// <summary>
    /// L'uploader di file in <c>ChatSdkService.SendFileAsync</c> divide in chunk da 2 MiB.
    /// Facciamo benchmark sopra e sotto quella soglia per vedere come scala il divario.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(16)]
    public async Task AesGcm_IsAtLeastSeveralTimesFasterThan_HmacCtr(int sizeMiB)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var aad = System.Text.Encoding.UTF8.GetBytes("echat-file-v1");
        var plaintext = RandomNumberGenerator.GetBytes(sizeMiB * 1024 * 1024);

        // Warm-up di entrambi i path, così JIT e cache CPU non falsano la prima run.
        await HmacCtrEncryptAsync(new byte[64 * 1024], key, aad);
        AesGcmEncrypt(new byte[64 * 1024], key, aad);

        var hmacCtrMs = await TimeAverageAsync(
            iterations: 3,
            action: async () => await HmacCtrEncryptAsync(plaintext, key, aad));

        var aesGcmMs = TimeAverage(
            iterations: 3,
            action: () => AesGcmEncrypt(plaintext, key, aad));

        var hmacCtrThroughput = sizeMiB * 1000.0 / hmacCtrMs;  // MiB/s
        var aesGcmThroughput  = sizeMiB * 1000.0 / aesGcmMs;
        var ratio = hmacCtrMs / aesGcmMs;

        _out.WriteLine(
            $"size={sizeMiB,3} MiB │ " +
            $"HMAC-CTR {hmacCtrMs,7:F1} ms ({hmacCtrThroughput,6:F1} MiB/s) │ " +
            $"AES-GCM  {aesGcmMs,7:F2} ms ({aesGcmThroughput,7:F1} MiB/s) │ " +
            $"ratio {ratio,6:F1}×");

        // Soglia conservativa: AES-NI rispetto a HMAC-SHA256-CTR deve essere almeno 5x anche
        // sull'hardware CI più lento. La misura vera è stampata sopra; questa assertion serve
        // solo a fallire rumorosamente in caso di regressione grossa (es. AES-NI assente sul
        // runner: in tal caso la dichiarazione della doc non vale per quell'hardware).
        ratio.Should().BeGreaterThan(5.0,
            "AES-NI hardware AES-GCM should be at least 5× faster than software HMAC-CTR; " +
            "the docs claim ~50× in WASM, where the gap widens further");
    }

    /// <summary>
    /// Cifra una volta 64 MiB e stampa il throughput. Più grande del massimo di 16 MiB del
    /// test parametrico, così le costanti per blocco si ammortizzano: più simile a ciò
    /// che vede <c>SendFileAsync</c> sugli allegati reali.
    /// </summary>
    [Fact]
    public async Task LargePayload_RatioReport()
    {
        const int sizeMiB = 64;
        var key = RandomNumberGenerator.GetBytes(32);
        var aad = System.Text.Encoding.UTF8.GetBytes("echat-file-v1");
        var plaintext = RandomNumberGenerator.GetBytes(sizeMiB * 1024 * 1024);

        await HmacCtrEncryptAsync(new byte[64 * 1024], key, aad);
        AesGcmEncrypt(new byte[64 * 1024], key, aad);

        var sw = Stopwatch.StartNew();
        await HmacCtrEncryptAsync(plaintext, key, aad);
        sw.Stop();
        var hmacCtrMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        AesGcmEncrypt(plaintext, key, aad);
        sw.Stop();
        var aesGcmMs = sw.Elapsed.TotalMilliseconds;

        _out.WriteLine($"=== {sizeMiB} MiB single-shot ===");
        _out.WriteLine($"HMAC-CTR : {hmacCtrMs,8:F1} ms  ({sizeMiB * 1000.0 / hmacCtrMs:F1} MiB/s)");
        _out.WriteLine($"AES-GCM  : {aesGcmMs,8:F2} ms  ({sizeMiB * 1000.0 / aesGcmMs:F1} MiB/s)");
        _out.WriteLine($"Ratio    : {hmacCtrMs / aesGcmMs,6:F1}× (CLR; browser/WASM gap is wider)");

        (hmacCtrMs / aesGcmMs).Should().BeGreaterThan(5.0);
    }

    /// <summary>
    /// Copia fedele di <c>SimpleCryptoSuiteFactory.HmacCtrCipher.EncryptAsync</c>.
    /// Inlinata qui (invece di referenziata) perché Client.App è un progetto BlazorWebAssembly SDK
    /// e non è ProjectReference-abile da un progetto xUnit net10.0. Se cambia il cipher di
    /// produzione, aggiorna questa copia: l'algoritmo è volutamente semplice e il test
    /// dipende solo dalla forma del costo big-O, non dal byte-for-byte dell'output.
    /// </summary>
    private static async Task<byte[]> HmacCtrEncryptAsync(byte[] plaintext, byte[] key, byte[] aad)
    {
        var iv = RandomNumberGenerator.GetBytes(16);
        var combined = new byte[16 + plaintext.Length + 32];
        Buffer.BlockCopy(iv, 0, combined, 0, 16);

        using var tagHmac = new HMACSHA256(key);
        tagHmac.TransformBlock(aad, 0, aad.Length, null, 0);
        tagHmac.TransformBlock(iv, 0, iv.Length, null, 0);

        using var ksHmac = new HMACSHA256(key);
        var counterBuf = new byte[20];
        Buffer.BlockCopy(iv, 0, counterBuf, 0, 16);

        const int yieldBytes = 256 * 1024;
        int blockIndex = 0, offset = 0, sinceYield = 0;
        var total = plaintext.Length;

        while (offset < total)
        {
            counterBuf[16] = (byte)(blockIndex >> 24);
            counterBuf[17] = (byte)(blockIndex >> 16);
            counterBuf[18] = (byte)(blockIndex >> 8);
            counterBuf[19] = (byte)blockIndex;
            var ks = ksHmac.ComputeHash(counterBuf);

            var n = Math.Min(32, total - offset);
            var dst = 16 + offset;
            for (int i = 0; i < n; i++)
                combined[dst + i] = (byte)(plaintext[offset + i] ^ ks[i]);

            tagHmac.TransformBlock(combined, dst, n, null, 0);

            offset += n;
            blockIndex++;
            sinceYield += n;

            if (sinceYield >= yieldBytes)
            {
                sinceYield = 0;
                await Task.Yield();
            }
        }

        tagHmac.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        Buffer.BlockCopy(tagHmac.Hash!, 0, combined, 16 + total, 32);
        return combined;
    }

    /// <summary>
    /// AES-GCM nativo tramite <see cref="AesGcm"/> di .NET. Su x64 delega ad AES-NI:
    /// lo stesso path hardware che <c>crypto.subtle.encrypt(AES-GCM)</c> usa nel browser.
    /// </summary>
    private static byte[] AesGcmEncrypt(byte[] plaintext, byte[] key, byte[] aad)
    {
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        var ciphertext = new byte[plaintext.Length];

        using var gcm = new AesGcm(key, AesGcm.TagByteSizes.MaxSize);
        gcm.Encrypt(nonce, plaintext, ciphertext, tag, aad);

        var result = new byte[nonce.Length + ciphertext.Length + tag.Length];
        Buffer.BlockCopy(nonce, 0, result, 0, nonce.Length);
        Buffer.BlockCopy(ciphertext, 0, result, nonce.Length, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, result, nonce.Length + ciphertext.Length, tag.Length);
        return result;
    }

    private static async Task<double> TimeAverageAsync(int iterations, Func<Task> action)
    {
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++) await action();
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds / iterations;
    }

    private static double TimeAverage(int iterations, Action action)
    {
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++) action();
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds / iterations;
    }
}
