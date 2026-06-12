using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using ECHAT.Client.App;
using ECHAT.Client.App.Services;
using ECHAT.Client.Core.Interfaces;
using ECHAT.Client.Core.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// HttpClient sulla stessa origin: Server.App ospita il client
builder.Services.AddScoped(sp => new HttpClient
{
    BaseAddress = new Uri(builder.HostEnvironment.BaseAddress)
});

// Persistenza su localStorage
builder.Services.AddSingleton<LocalStorageService>();

// Stato di autenticazione (JWT da localStorage)
builder.Services.AddSingleton<ITokenParser, JwtTokenParser>();
builder.Services.AddScoped<TokenAuthStateProvider>();
builder.Services.AddScoped<AuthenticationStateProvider>(sp => sp.GetRequiredService<TokenAuthStateProvider>());
builder.Services.AddAuthorizationCore();

// Servizi di Client.Core (logica pura, con storage persistente)
builder.Services.AddSingleton<IOutboxStore, LocalStorageOutboxStore>();
builder.Services.AddSingleton<IOutbox>(sp => new OutboxService(sp.GetRequiredService<IOutboxStore>()));
builder.Services.AddSingleton<ISequenceLeaseManager, SequenceLeaseManager>();
builder.Services.AddSingleton<IChainValidator, ChainValidator>();

// Implementazioni crypto + E2EE. Tutta la crypto simmetrica gira su WebCrypto (browser):
// AES-GCM + HMAC via JS. L'engine compone cipher/signer/compressor; l'hash envelope resta C#
// (EnvelopeHasher, condiviso col server). Scoped perché cipher/signer dipendono da IJSRuntime.
builder.Services.AddScoped<IAeadCipher, JsAeadCipher>();
builder.Services.AddScoped<ISigner, JsSigner>();
builder.Services.AddSingleton<ICompressor, GzipCompressor>();
builder.Services.AddScoped<ICryptoEngine, AeadCryptoEngine>();
builder.Services.AddSingleton<IDeviceKeyStore, LocalStorageDeviceKeyStore>();
builder.Services.AddSingleton<ILocalStorageTransport, LocalStorageTransport>();
builder.Services.AddSingleton<ILocalStore, BrowserLocalStoreImpl>();

// Trasferimento file + scroll loader + custodian + file cipher
builder.Services.AddSingleton<IFileTransferStrategy, FileTransferStrategy>();
builder.Services.AddScoped<IFileTransferManager, HttpFileTransferManager>();
// Offload crypto di rotazione: l'engine è già backed-by-WebCrypto, quindi decrypt/encrypt/sign
// girano fuori dal main thread via il crypto-worker. Un'unica implementazione (niente più runtime
// .NET isolato in un Web Worker dedicato).
builder.Services.AddScoped<ICustodianCryptoOffloader, InProcessCustodianCryptoOffloader>();
builder.Services.AddScoped<ICustodianWorker, ECHAT.Client.Core.Services.CustodianWorker>();
builder.Services.AddScoped<FileCipher>(sp => new FileCipher(sp.GetRequiredService<IAeadCipher>()));
builder.Services.AddScoped<WorkerFileEncryptor>();
builder.Services.AddScoped<IFileBodyCipher>(sp => sp.GetRequiredService<WorkerFileEncryptor>());

// Realtime + ChatSdk
builder.Services.AddScoped<SignalRRealtimeClient>();
builder.Services.AddScoped<IRealtimeClient>(sp => sp.GetRequiredService<SignalRRealtimeClient>());
// Tracker delle migrazioni in corso: condiviso tra ChatSdk (blocca SendMessage) e Chat.razor
// (mostra banner + disabilita composer). Sottoscrive da solo OnJobProgress per riflettere
// le saghe pilotate da altri device sulla stessa conversazione.
builder.Services.AddSingleton<IMigrationStateManager, MigrationStateManager>();
builder.Services.AddScoped<IMigrationStateTracker, MigrationStateTracker>();
builder.Services.AddScoped<IChatServerGateway, HttpChatServerGateway>();
builder.Services.AddScoped<MessageFlowOrchestrator>();
builder.Services.AddScoped<CekProvisioner>();
builder.Services.AddScoped<DeviceEnrollmentService>();
builder.Services.AddScoped<FileEncryptionOrchestrator>();
builder.Services.AddScoped<ChatSdkService>();
builder.Services.AddScoped<IChatSdk>(sp => sp.GetRequiredService<ChatSdkService>());
builder.Services.AddScoped<IScrollLoader, ScrollLoader>();

// Risposta automatica AI
builder.Services.AddScoped<IAiReplyGenerator, HttpAiReplyGenerator>();
builder.Services.AddSingleton<IDelayProvider, RandomDelayProvider>();
builder.Services.AddScoped<IAiAutoReplyOrchestrator, AiAutoReplyOrchestrator>();
builder.Services.AddScoped<AiAutoReplyService>();

// Rendering Markdown / HTML per il testo dei messaggi
builder.Services.AddSingleton<IMessageRenderer, MessageRenderer>();

await builder.Build().RunAsync();
