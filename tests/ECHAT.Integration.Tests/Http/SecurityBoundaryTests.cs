using System.Net;
using System.Net.Http.Json;
using ECHAT.Models.Domain;
using ECHAT.Models.Dtos;
using ECHAT.Models.Enums;
using FluentAssertions;

namespace ECHAT.Integration.Tests.Http;

/// <summary>
/// Test E2E al confine HTTP REALE (WebApplicationFactory): controllers + auth filters + ingest
/// pipeline + CoreExceptionFilter, con DbContext InMemory. Verifica l'enforcement S1-S4:
///   S3 = firma ECDSA dell'envelope (SignatureVerificationHandler)
///   S4 = binding identità mittente al JWT + directory device (SenderIdentityHandler)
/// più i gate di autorizzazione conversation-scoped e la mappatura del CoreExceptionFilter.
/// </summary>
public class SecurityBoundaryTests : IClassFixture<EchatWebAppFactory>
{
    private readonly EchatWebAppFactory _factory;
    private readonly Seeder _seed;

    public SecurityBoundaryTests(EchatWebAppFactory factory)
    {
        _factory = factory;
        _seed = new Seeder(factory);
    }

    // ---------------------------------------------------------------------------------------------
    // Autenticazione
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Unauthenticated_request_to_protected_endpoint_is_401()
    {
        var client = _factory.AnonymousClient();

        var res = await client.GetAsync("/api/conversations");

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---------------------------------------------------------------------------------------------
    // Device directory (S4 anchor)
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Register_device_then_GET_me_returns_it()
    {
        var userId = _seed.SeedUser();
        using var device = new SigningDevice();
        var client = _factory.AuthedClient(userId);

        var register = await client.PostAsJsonAsync("/api/devices/register", new DeviceRegistration
        {
            DeviceId = device.DeviceId,
            RsaOaepSpki = device.RsaOaepSpki,
            EcdsaSpki = device.EcdsaSpki,
        });
        register.StatusCode.Should().Be(HttpStatusCode.OK);

        var me = await client.GetFromJsonAsync<List<DevicePublicKey>>("/api/devices/me");
        me.Should().NotBeNull();
        me!.Should().ContainSingle(d => d.DeviceId == device.DeviceId && d.UserId == userId);
    }

    // ---------------------------------------------------------------------------------------------
    // Conversation-scoped authorization: POST /keys richiede AddMember (Owner/Admin)
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Owner_can_post_keys_but_plain_member_gets_403()
    {
        var ownerId = Guid.NewGuid();
        var memberId = Guid.NewGuid();
        var conversationId = _seed.SeedConversation(ownerId, members: new[] { (memberId, "Member") });

        var ownerClient = _factory.AuthedClient(ownerId);
        var memberClient = _factory.AuthedClient(memberId);

        // Wrap reale per un membro attivo (modello: DeviceId == userId). Target = ownerId (membro attivo).
        using var device = new SigningDevice();
        var wraps = new List<WrappedKey>
        {
            new()
            {
                ConversationId = conversationId,
                EpochId = 1,
                DeviceId = ownerId, // target: deve essere un membro attivo
                WrappedCek = device.WrapCek(new byte[32]),
                KeyWrapVersion = 1,
            }
        };

        var ownerPost = await ownerClient.PostAsJsonAsync($"/api/conversations/{conversationId}/keys", wraps);
        ownerPost.StatusCode.Should().Be(HttpStatusCode.NoContent); // 204

        // Il gate di ruolo (RequireConversationPermission(AddMember)) corto-circuita PRIMA del body,
        // quindi il payload del membro è irrilevante: deve comunque ricevere 403.
        var memberPost = await memberClient.PostAsJsonAsync($"/api/conversations/{conversationId}/keys", wraps);
        memberPost.StatusCode.Should().Be(HttpStatusCode.Forbidden); // 403: RequireConversationPermission(AddMember)
    }

    // ---------------------------------------------------------------------------------------------
    // Send message: S3 (firma) + S4 (identità mittente) all'ingest reale
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Send_with_valid_signed_envelope_from_registered_device_is_200()
    {
        var (userId, conversationId, device, client) = await SetupSenderAsync();

        var envelope = await BuildSignedEnvelopeAsync(client, conversationId, userId, device);

        var res = await client.PostAsJsonAsync($"/api/conversations/{conversationId}/messages", envelope);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var ack = await res.Content.ReadFromJsonAsync<MessageAck>();
        ack!.Seq.Should().Be(envelope.Seq);
    }

    [Fact]
    public async Task Send_with_forged_signature_is_403_S3()
    {
        var (userId, conversationId, device, client) = await SetupSenderAsync();

        var envelope = await BuildSignedEnvelopeAsync(client, conversationId, userId, device);
        // Forgia: corrompi la firma valida (stessa lunghezza, byte diversi)  verifica ECDSA fallisce.
        var forged = (byte[])envelope.Signature.Clone();
        forged[0] ^= 0xFF;
        forged[^1] ^= 0xFF;
        var tampered = Clone(envelope, signature: forged);

        var res = await client.PostAsJsonAsync($"/api/conversations/{conversationId}/messages", tampered);

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden); // SignatureVerificationHandler  ForbiddenException
    }

    [Fact]
    public async Task Send_with_sender_user_not_matching_jwt_is_403_S4()
    {
        var (userId, conversationId, device, client) = await SetupSenderAsync();

        // Envelope che dichiara un SenderUserId diverso dal principal del JWT.
        var otherUser = Guid.NewGuid();
        var envelope = await BuildSignedEnvelopeAsync(client, conversationId, userId, device,
            overrideSenderUserId: otherUser);

        var res = await client.PostAsJsonAsync($"/api/conversations/{conversationId}/messages", envelope);

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden); // SenderIdentityHandler  ForbiddenException
    }

    [Fact]
    public async Task Send_with_unregistered_sender_device_is_403_S4()
    {
        var (userId, conversationId, _, client) = await SetupSenderAsync();

        // Device mai registrato nella directory: SenderIdentityHandler non lo trova  403.
        using var rogueDevice = new SigningDevice();
        var envelope = await BuildSignedEnvelopeAsync(client, conversationId, userId, rogueDevice);

        var res = await client.PostAsJsonAsync($"/api/conversations/{conversationId}/messages", envelope);

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden); // device non posseduto dall'utente  ForbiddenException
    }

    // ---------------------------------------------------------------------------------------------
    // GET conversation devices: membro OK, non-membro 403
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Member_can_list_conversation_devices_nonmember_is_403()
    {
        var ownerId = Guid.NewGuid();
        var conversationId = _seed.SeedConversation(ownerId);
        using var device = new SigningDevice();
        _seed.RegisterDevice(ownerId, device);

        var ownerClient = _factory.AuthedClient(ownerId);
        var devices = await ownerClient.GetFromJsonAsync<List<DevicePublicKey>>(
            $"/api/conversations/{conversationId}/devices");
        devices.Should().NotBeNull();
        devices!.Should().Contain(d => d.DeviceId == device.DeviceId);

        var outsider = _seed.SeedUser();
        var outsiderClient = _factory.AuthedClient(outsider);
        var res = await outsiderClient.GetAsync($"/api/conversations/{conversationId}/devices");
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden); // RequireConversationPermission(Read)  403
    }

    // ---------------------------------------------------------------------------------------------
    // CoreExceptionFilter: NotFoundException  404
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task CoreExceptionFilter_maps_NotFoundException_to_404()
    {
        // Membership orfana: il filtro Read passa (membership esiste) ma GetAsync lancia
        // NotFoundException perché la ConversationEntity non c'è  CoreExceptionFilter  404.
        var userId = Guid.NewGuid();
        var conversationId = _seed.SeedMembershipWithoutConversation(userId);
        var client = _factory.AuthedClient(userId);

        var res = await client.GetAsync($"/api/conversations/{conversationId}");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---------------------------------------------------------------------------------------------
    // Helper
    // ---------------------------------------------------------------------------------------------

    private async Task<(Guid userId, Guid conversationId, SigningDevice device, HttpClient client)> SetupSenderAsync()
    {
        var userId = Guid.NewGuid();
        var conversationId = _seed.SeedConversation(userId); // userId = Owner  Write permission
        var device = new SigningDevice();
        _seed.RegisterDevice(userId, device);
        var client = _factory.AuthedClient(userId);
        return (userId, conversationId, device, client);
    }

    /// <summary>
    /// Riserva un lease reale via /seq/reserve, costruisce l'envelope con quel seq/lease e lo firma.
    /// </summary>
    private static async Task<MessageEnvelope> BuildSignedEnvelopeAsync(
        HttpClient client, Guid conversationId, Guid senderUserId, SigningDevice device,
        Guid? overrideSenderUserId = null)
    {
        var reservation = await ReserveSeqAsync(client, conversationId);

        var envelope = new MessageEnvelope
        {
            ConversationId = conversationId,
            MessageId = Guid.NewGuid(),
            Seq = reservation.StartSeq,
            EpochId = 1,
            SenderDeviceId = device.DeviceId,
            SenderUserId = overrideSenderUserId ?? senderUserId,
            Nonce = new byte[12],
            Ciphertext = new byte[] { 1, 2, 3, 4, 5 },
            LeaseToken = reservation.LeaseToken,
            Type = MessageType.Text,
            CreatedAt = DateTime.UtcNow,
        };

        return Clone(envelope, signature: device.Sign(envelope));
    }

    private static async Task<SeqReservation> ReserveSeqAsync(HttpClient client, Guid conversationId)
    {
        var res = await client.PostAsync($"/api/conversations/{conversationId}/seq/reserve?count=4", content: null);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await res.Content.ReadFromJsonAsync<SeqReservation>())!;
    }

    private static MessageEnvelope Clone(MessageEnvelope e, byte[]? signature = null) => new()
    {
        ConversationId = e.ConversationId,
        MessageId = e.MessageId,
        Seq = e.Seq,
        EpochId = e.EpochId,
        SenderDeviceId = e.SenderDeviceId,
        SenderUserId = e.SenderUserId,
        Nonce = e.Nonce,
        Ciphertext = e.Ciphertext,
        Signature = signature ?? e.Signature,
        LeaseToken = e.LeaseToken,
        Type = e.Type,
        CreatedAt = e.CreatedAt,
    };
}
