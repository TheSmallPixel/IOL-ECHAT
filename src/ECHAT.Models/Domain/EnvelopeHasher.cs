using System.Security.Cryptography;
using System.Text;

namespace ECHAT.Models.Domain;

public static class EnvelopeHasher
{
    public static byte[] Compute(MessageEnvelope envelope)
    {
        // Il digest è il pre-image firmato dal mittente (S3) E l'anello della chain (prevEnvelopeHash).
        // Include il Nonce così la firma copre l'INTERA struttura dell'envelope: senza, un nonce
        // diverso non cambierebbe la firma (il nonce sarebbe legato solo implicitamente via il MAC GCM).
        var nonce = envelope.Nonce ?? Array.Empty<byte>();
        var ciphertext = envelope.Ciphertext ?? Array.Empty<byte>();
        var canonical = $"{envelope.ConversationId}|{envelope.MessageId}|{envelope.Seq}|{envelope.EpochId}|{envelope.SenderDeviceId}|{Convert.ToBase64String(nonce)}";
        var data = Encoding.UTF8.GetBytes(canonical);
        var withCiphertext = new byte[data.Length + ciphertext.Length];
        Buffer.BlockCopy(data, 0, withCiphertext, 0, data.Length);
        Buffer.BlockCopy(ciphertext, 0, withCiphertext, data.Length, ciphertext.Length);
        return SHA256.HashData(withCiphertext);
    }
}
