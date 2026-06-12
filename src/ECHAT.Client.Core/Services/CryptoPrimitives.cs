using System.IO.Compression;
using ECHAT.Client.Core.Interfaces;

namespace ECHAT.Client.Core.Services;

/// <summary>
/// Compressione gzip dei payload. È l'unico primitivo della pipeline che gira in C# anche in
/// produzione (browser WASM): gzip è WASM-safe e non è crittografia. Cipher e signer sono invece
/// implementati solo lato JS (WebCrypto); nei test C# si usano fake che fanno round-trip.
/// </summary>
public class GzipCompressor : ICompressor
{
    /// <summary>Prefissi MIME il cui payload è già compresso; gzippare sprecherebbe solo CPU.</summary>
    private static readonly string[] PrecompressedPrefixes = new[]
    {
        "image/jpeg", "image/png", "image/gif", "image/webp", "image/avif",
        "video/", "audio/",
        "application/zip", "application/x-7z-compressed", "application/gzip",
        "application/x-rar-compressed", "application/x-bzip2", "application/x-zstd",
        "application/pdf"
    };

    public byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gz = new GZipStream(output, CompressionLevel.Fastest, true))
            gz.Write(data, 0, data.Length);
        return output.ToArray();
    }

    public byte[] Decompress(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var gz = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gz.CopyTo(output);
        return output.ToArray();
    }

    public bool ShouldCompress(string? mimeType, long size)
    {
        if (size <= 128) return false;
        if (string.IsNullOrEmpty(mimeType)) return true;
        var lower = mimeType.ToLowerInvariant();
        return !PrecompressedPrefixes.Any(p => lower.StartsWith(p));
    }
}
