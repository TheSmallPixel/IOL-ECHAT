namespace ECHAT.Server.Core.Services;

/// <summary>
/// Algoritmo puro di assemblaggio dei blob in parti: nomi di parte con zero-padding, ordinamento
/// per nome, accumulo della dimensione totale. L'I/O reale (CreateDirectory, Read/WriteAllBytes,
/// Move, Delete) e il binding della configurazione restano nella App.
/// </summary>
public class BlobFileAssemblyService
{
    /// <summary>
    /// Costruisce il path di una parte sotto <paramref name="tempDir"/> usando lo stesso schema
    /// con zero-padding a 5 cifre usato in fase di scrittura (<c>part-00000</c>, <c>part-00001</c>, ...),
    /// così l'ordinamento lessicografico dei nomi coincide con l'ordine numerico delle parti.
    /// </summary>
    public string GetPartPath(string tempDir, int partNo)
        => Path.Combine(tempDir, $"part-{partNo:D5}");

    /// <summary>
    /// Ordina i path delle parti in ordine lessicografico (equivalente all'ordine numerico grazie
    /// allo zero-padding), indipendentemente dall'ordine di enumerazione del filesystem.
    /// </summary>
    public IReadOnlyList<string> OrderPartPaths(IEnumerable<string> partPaths)
        => partPaths.OrderBy(f => f).ToArray();

    /// <summary>
    /// Somma la lunghezza delle parti nell'ordine fornito: corrisponde alla dimensione del blob
    /// assemblato.
    /// </summary>
    public long CalculateAssembledSize(IEnumerable<byte[]> parts)
    {
        long total = 0;
        foreach (var part in parts)
            total += part.Length;
        return total;
    }
}
