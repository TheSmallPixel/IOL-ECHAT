using System.Security.Claims;
using ECHAT.Models.Dtos;
using ECHAT.Models.Enums;
using ECHAT.Server.App.Authorization;
using ECHAT.Server.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ECHAT.Server.App.Controllers;

/// <summary>
/// Directory delle chiavi pubbliche per device (S1-S4). Registrazione legata allo UserId del JWT;
/// lookup dei device propri e dei device dei membri di una conversazione (per il wrapping della CEK).
/// </summary>
[Authorize]
[ApiController]
public class DevicesController : ControllerBase
{
    private readonly DeviceDirectoryService _devices;
    private readonly ECHAT.Server.Core.Interfaces.IMessageRepository _messages;

    public DevicesController(DeviceDirectoryService devices, ECHAT.Server.Core.Interfaces.IMessageRepository messages)
    {
        _devices = devices;
        _messages = messages;
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);

    /// <summary>Registra/ri-registra le chiavi pubbliche di questo device per l'utente autenticato.</summary>
    [HttpPost("api/devices/register")]
    public async Task<IActionResult> Register([FromBody] DeviceRegistration registration)
    {
        await _devices.RegisterAsync(GetUserId(), registration);
        return Ok(new { message = "Device registered." });
    }

    /// <summary>I device attivi dell'utente autenticato (per capire se questo browser è già enrolled).</summary>
    [HttpGet("api/devices/me")]
    public async Task<ActionResult<List<DevicePublicKey>>> MyDevices()
        => Ok(await _devices.GetMyDevicesAsync(GetUserId()));

    /// <summary>I device pubblici di tutti i membri di una conversazione (per wrappare la CEK).</summary>
    [HttpGet("api/conversations/{conversationId}/devices")]
    [RequireConversationPermission(Permission.Read)]
    public async Task<ActionResult<List<DevicePublicKey>>> ConversationDevices(Guid conversationId)
        => Ok(await _devices.GetConversationDevicesAsync(conversationId));

    /// <summary>
    /// Chiave pubblica di un singolo device che ha inviato messaggi IN QUESTA conversazione, per
    /// verificare la firma di mittenti storici non più tra i membri attivi (es. rimossi). Scopato alla
    /// conversazione (Read) + al fatto che il device vi abbia davvero scritto: niente lookup/enumerazione
    /// globale di chiavi pubbliche arbitrarie. Stare sotto /api/conversations/... elimina anche ogni
    /// ambiguità di route con /api/devices/me.
    /// </summary>
    [HttpGet("api/conversations/{conversationId}/devices/{deviceId:guid}")]
    [RequireConversationPermission(Permission.Read)]
    public async Task<ActionResult<DevicePublicKey>> ConversationSenderDevice(Guid conversationId, Guid deviceId)
    {
        if (!await _messages.HasMessageFromDeviceAsync(conversationId, deviceId))
            return NotFound(); // il device non ha inviato qui  non esponiamo la sua chiave a questo chiamante
        var device = await _devices.GetDeviceAsync(deviceId);
        return device is null ? NotFound() : Ok(device);
    }
}
