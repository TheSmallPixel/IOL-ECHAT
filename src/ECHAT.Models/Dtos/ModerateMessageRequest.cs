namespace ECHAT.Models.Dtos;

/// <summary>Corpo di POST /api/conversations/{id}/messages/{seq}/moderate.</summary>
public class ModerateMessageRequest
{
    /// <summary>true = nascondi il messaggio, false = ri-mostralo.</summary>
    public bool Hidden { get; set; }
    /// <summary>Motivo opzionale (mostrato nel placeholder e registrato nell'audit).</summary>
    public string? Reason { get; set; }
}
