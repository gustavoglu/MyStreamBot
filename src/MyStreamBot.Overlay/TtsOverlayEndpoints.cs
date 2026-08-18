using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MyStreamBot.Core.Entities;
using MyStreamBot.Infrastructure.Persistence;

namespace MyStreamBot.Overlay;

public static class TtsOverlayEndpoints
{
    private static readonly SemaphoreSlim SchemaGate = new(1, 1);
    private const int MaxEvents = 1000;

    public static void MapTtsOverlay(this WebApplication app)
    {
        var logger = app.Logger;

        app.MapPost("/api/tts", async (TtsOverlayPublishRequest request, IDbContextFactory<MyStreamBotDbContext> factory, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Text) || string.IsNullOrWhiteSpace(request.AudioFileName))
                return Results.BadRequest(new { error = "username, text e audioFileName são obrigatórios." });
            var safeFileName = Path.GetFileName(request.AudioFileName);
            if (!string.Equals(safeFileName, request.AudioFileName, StringComparison.Ordinal)) return Results.BadRequest(new { error = "Nome de arquivo inválido." });
            var audioPath = Path.Combine(GetAudioDirectory(), safeFileName);
            if (!File.Exists(audioPath))
            {
                logger.LogWarning("[TTS-OVERLAY] PUBLISH_REJECTED AudioFileMissing User={Username} File={FileName}", request.Username, safeFileName);
                return Results.NotFound(new { error = "Áudio ainda não está disponível.", fileName = safeFileName });
            }

            await EnsureSchemaAsync(factory, ct);
            await using var db = await factory.CreateDbContextAsync(ct);
            var item = new TtsOverlayEvent
            {
                Username = request.Username.Trim(), AvatarUrl = request.AvatarUrl,
                Text = request.Text.Trim(), AudioFileName = safeFileName,
                CreatedAtUtc = DateTime.UtcNow, IsRepeat = false
            };
            db.TtsOverlayEvents.Add(item);
            await db.SaveChangesAsync(ct);
            await TrimEventsAsync(db, ct);
            logger.LogInformation("[TTS-OVERLAY] PUBLISHED EventId={EventId} User={Username} File={FileName} Text={Text}", item.Id, item.Username, item.AudioFileName, item.Text);
            return Results.Ok(new { id = item.Id });
        });

        app.MapGet("/api/tts/current", async (string? client, IDbContextFactory<MyStreamBotDbContext> factory, CancellationToken ct) =>
        {
            await EnsureSchemaAsync(factory, ct);
            var clientName = NormalizeClient(client);
            await using var db = await factory.CreateDbContextAsync(ct);
            var currentId = await db.TtsOverlayEvents.AsNoTracking().Select(x => (long?)x.Id).MaxAsync(ct) ?? 0;
            var state = await GetOrCreateClientAsync(db, clientName, currentId, initializeIfMissing: true, ct);
            return Results.Ok(new { client = clientName, currentId, lastEventId = state.LastEventId });
        });

        app.MapGet("/api/tts", async (string? client, IDbContextFactory<MyStreamBotDbContext> factory, CancellationToken ct) =>
        {
            await EnsureSchemaAsync(factory, ct);
            var clientName = NormalizeClient(client);
            await using var db = await factory.CreateDbContextAsync(ct);
            var currentId = await db.TtsOverlayEvents.AsNoTracking().Select(x => (long?)x.Id).MaxAsync(ct) ?? 0;
            var state = await GetOrCreateClientAsync(db, clientName, currentId, initializeIfMissing: true, ct);
            var items = await db.TtsOverlayEvents.AsNoTracking().Where(x => x.Id > state.LastEventId).OrderBy(x => x.Id).Take(20).ToArrayAsync(ct);
            if (items.Length > 0)
                logger.LogInformation("[TTS-OVERLAY] POLL_RETURNED Client={Client} AfterId={AfterId} Count={Count} FirstId={FirstId} LastId={LastId}", clientName, state.LastEventId, items.Length, items[0].Id, items[^1].Id);
            return Results.Ok(items);
        });

        app.MapPost("/api/tts/{id:long}/ack", async (long id, string? client, TtsOverlayAckRequest request, IDbContextFactory<MyStreamBotDbContext> factory, CancellationToken ct) =>
        {
            await EnsureSchemaAsync(factory, ct);
            var clientName = NormalizeClient(client);
            await using var db = await factory.CreateDbContextAsync(ct);
            var exists = await db.TtsOverlayEvents.AsNoTracking().AnyAsync(x => x.Id == id, ct);
            if (!exists) return Results.NotFound(new { error = "Evento TTS não encontrado.", id });
            var state = await db.TtsOverlayClients.SingleOrDefaultAsync(x => x.ClientName == clientName, ct);
            if (state is null) state = new TtsOverlayClient { ClientName = clientName };
            if (id > state.LastEventId) state.LastEventId = id;
            state.UpdatedAtUtc = DateTime.UtcNow;
            if (state.Id == 0) db.TtsOverlayClients.Add(state);
            await db.SaveChangesAsync(ct);
            if (string.Equals(request.Stage, "PLAY_STARTED", StringComparison.OrdinalIgnoreCase)) logger.LogInformation("[TTS-OVERLAY] SHOWN Client={Client} EventId={EventId} Stage=PLAY_STARTED", clientName, id);
            else if (string.Equals(request.Stage, "PLAY_FINISHED", StringComparison.OrdinalIgnoreCase)) logger.LogInformation("[TTS-OVERLAY] PLAY_FINISHED Client={Client} EventId={EventId}", clientName, id);
            else if (string.Equals(request.Stage, "PLAY_ERROR", StringComparison.OrdinalIgnoreCase)) logger.LogError("[TTS-OVERLAY] PLAY_ERROR Client={Client} EventId={EventId} Error={Error}", clientName, id, request.Error ?? "unknown");
            return Results.Ok(new { id, client = clientName, stage = request.Stage, lastEventId = state.LastEventId });
        });

        app.MapGet("/api/tts/clients", async (IDbContextFactory<MyStreamBotDbContext> factory, CancellationToken ct) =>
        {
            await EnsureSchemaAsync(factory, ct);
            await using var db = await factory.CreateDbContextAsync(ct);
            return Results.Ok(await db.TtsOverlayClients.AsNoTracking().OrderBy(x => x.ClientName).ToArrayAsync(ct));
        });

        app.MapPost("/api/tts/repeat-last", async (IDbContextFactory<MyStreamBotDbContext> factory, CancellationToken ct) =>
        {
            await EnsureSchemaAsync(factory, ct);
            await using var db = await factory.CreateDbContextAsync(ct);
            var original = await db.TtsOverlayEvents.OrderByDescending(x => x.Id).FirstOrDefaultAsync(x => !x.IsRepeat, ct);
            return original is null ? Results.NotFound(new { error = "Nenhum TTS disponível para repetir." }) : Results.Ok(await CreateRepeatAsync(db, original, ct));
        });

        app.MapPost("/api/tts/repeat-user/{username}", async (string username, IDbContextFactory<MyStreamBotDbContext> factory, CancellationToken ct) =>
        {
            await EnsureSchemaAsync(factory, ct);
            await using var db = await factory.CreateDbContextAsync(ct);
            var original = await db.TtsOverlayEvents.Where(x => !x.IsRepeat && x.Username.ToLower() == username.ToLower()).OrderByDescending(x => x.Id).FirstOrDefaultAsync(ct);
            return original is null ? Results.NotFound(new { error = "Nenhum TTS encontrado para este viewer.", username }) : Results.Ok(await CreateRepeatAsync(db, original, ct));
        });

        app.MapGet("/api/tts/audio/{fileName}", (string fileName) =>
        {
            var safeFileName = Path.GetFileName(fileName);
            if (!string.Equals(safeFileName, fileName, StringComparison.Ordinal)) return Results.BadRequest();
            var path = Path.Combine(GetAudioDirectory(), safeFileName);
            if (!File.Exists(path)) { logger.LogWarning("[TTS-OVERLAY] AUDIO_NOT_FOUND File={FileName}", safeFileName); return Results.NotFound(); }
            logger.LogDebug("[TTS-OVERLAY] AUDIO_REQUESTED File={FileName}", safeFileName);
            return Results.File(path, "audio/mpeg", enableRangeProcessing: true);
        });

        app.MapGet("/tts", () => Results.Content(TtsHtml(), "text/html; charset=utf-8"));
    }

    private static async Task<TtsOverlayClient> GetOrCreateClientAsync(MyStreamBotDbContext db, string clientName, long currentId, bool initializeIfMissing, CancellationToken ct)
    {
        var state = await db.TtsOverlayClients.SingleOrDefaultAsync(x => x.ClientName == clientName, ct);
        if (state is not null) return state;
        state = new TtsOverlayClient { ClientName = clientName, LastEventId = initializeIfMissing ? currentId : 0, UpdatedAtUtc = DateTime.UtcNow };
        db.TtsOverlayClients.Add(state);
        await db.SaveChangesAsync(ct);
        return state;
    }

    private static async Task<object> CreateRepeatAsync(MyStreamBotDbContext db, TtsOverlayEvent original, CancellationToken ct)
    {
        var repeat = new TtsOverlayEvent { Username = original.Username, AvatarUrl = original.AvatarUrl, Text = original.Text, AudioFileName = original.AudioFileName, CreatedAtUtc = DateTime.UtcNow, IsRepeat = true };
        db.TtsOverlayEvents.Add(repeat);
        await db.SaveChangesAsync(ct);
        await TrimEventsAsync(db, ct);
        return new { id = repeat.Id, username = repeat.Username, text = repeat.Text, audioFileName = repeat.AudioFileName };
    }

    private static async Task TrimEventsAsync(MyStreamBotDbContext db, CancellationToken ct)
    {
        var ids = await db.TtsOverlayEvents.AsNoTracking().OrderByDescending(x => x.Id).Skip(MaxEvents).Select(x => x.Id).ToArrayAsync(ct);
        if (ids.Length == 0) return;
        var old = await db.TtsOverlayEvents.Where(x => ids.Contains(x.Id)).ToListAsync(ct);
        db.TtsOverlayEvents.RemoveRange(old);
        await db.SaveChangesAsync(ct);
    }

    private static async Task EnsureSchemaAsync(IDbContextFactory<MyStreamBotDbContext> factory, CancellationToken ct)
    {
        if (!await SchemaGate.WaitAsync(0, ct)) return;
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await db.Database.ExecuteSqlRawAsync("CREATE TABLE IF NOT EXISTS TtsOverlayEvents (Id INTEGER NOT NULL CONSTRAINT PK_TtsOverlayEvents PRIMARY KEY AUTOINCREMENT, Username TEXT NOT NULL, AvatarUrl TEXT NULL, Text TEXT NOT NULL, AudioFileName TEXT NOT NULL, CreatedAtUtc TEXT NOT NULL, IsRepeat INTEGER NOT NULL DEFAULT 0); CREATE INDEX IF NOT EXISTS IX_TtsOverlayEvents_CreatedAtUtc ON TtsOverlayEvents (CreatedAtUtc); CREATE TABLE IF NOT EXISTS TtsOverlayClients (Id INTEGER NOT NULL CONSTRAINT PK_TtsOverlayClients PRIMARY KEY AUTOINCREMENT, ClientName TEXT NOT NULL, LastEventId INTEGER NOT NULL DEFAULT 0, UpdatedAtUtc TEXT NOT NULL); CREATE UNIQUE INDEX IF NOT EXISTS IX_TtsOverlayClients_ClientName ON TtsOverlayClients (ClientName);", ct);
        }
        finally { SchemaGate.Release(); }
    }

    private static string NormalizeClient(string? client) => string.IsNullOrWhiteSpace(client) ? "default" : client.Trim().Length > 200 ? client.Trim()[..200] : client.Trim();
    private static string GetAudioDirectory() { var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyStreamBot", "tts-test"); Directory.CreateDirectory(directory); return directory; }

    private static string TtsHtml() => """
<!doctype html><html lang="pt-BR"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>MyStreamBot - Voz do Chat</title><style>
:root{color-scheme:dark}*{box-sizing:border-box}html,body{margin:0;padding:0;background:transparent;font-family:Arial,Helvetica,sans-serif;color:#fff}body{width:900px;height:260px;overflow:hidden}.wrap{width:100%;height:100%;display:flex;align-items:center;justify-content:center}.card{width:820px;min-height:150px;padding:18px 26px;position:relative;border:1px solid rgba(255,193,52,.78);border-radius:22px;background:rgba(15,15,17,.72);box-shadow:0 0 28px rgba(255,176,20,.25),inset 0 0 35px rgba(255,176,20,.05);backdrop-filter:blur(8px);opacity:0;transform:translateX(-70px) scale(.98);pointer-events:none}.card.show{animation:enter .45s cubic-bezier(.2,.8,.25,1) forwards}.card.hide{animation:exit .35s ease forwards}.badge{position:absolute;top:-16px;left:50%;transform:translateX(-50%);padding:7px 22px;border:1px solid #ffbd2e;border-radius:999px;background:#111;color:#ffbd2e;font-weight:900;font-size:15px;letter-spacing:.5px;white-space:nowrap}.avatar{position:absolute;left:22px;top:50%;transform:translateY(-50%);width:54px;height:54px;border-radius:50%;object-fit:cover;border:2px solid rgba(255,193,47,.75);background:rgba(255,193,47,.08)}.speaker{position:absolute;left:68px;top:50%;transform:translateY(-50%);font-size:20px;color:#ffc12f}.content{margin-left:105px;margin-right:115px}.text{font-size:31px;line-height:1.12;font-weight:850;word-break:break-word;text-shadow:0 0 15px rgba(255,193,47,.18)}.text .quote{color:#ffc12f}.user{margin-top:9px;color:#aeb5c2;font-size:16px}.user strong{color:#ffc12f}.equalizer{position:absolute;right:24px;top:50%;transform:translateY(-50%);display:flex;gap:4px;align-items:flex-end;height:48px}.bar{width:5px;background:#ffc12f;border-radius:5px;animation:equalize .7s ease-in-out infinite alternate}.bar:nth-child(1){height:15px;animation-delay:-.5s}.bar:nth-child(2){height:31px;animation-delay:-.2s}.bar:nth-child(3){height:44px;animation-delay:-.7s}.bar:nth-child(4){height:25px;animation-delay:-.1s}.bar:nth-child(5){height:38px;animation-delay:-.4s}.bar:nth-child(6){height:19px;animation-delay:-.6s}.status{margin-top:12px;color:#ffc12f;font-size:11px;font-weight:800;letter-spacing:1.3px;text-transform:uppercase}@keyframes enter{from{opacity:0;transform:translateX(-70px) scale(.98)}to{opacity:1;transform:translateX(0) scale(1)}}@keyframes exit{from{opacity:1;transform:translateX(0)}to{opacity:0;transform:translateX(40px)}}@keyframes equalize{to{transform:scaleY(.35)}}
</style></head><body><div class="wrap"><div id="card" class="card"><div class="badge">🔊 O CHAT MANDOU!</div><img id="avatar" class="avatar" alt=""><div class="speaker">🔊</div><div class="content"><div id="text" class="text"></div><div id="user" class="user"></div><div class="status">LENDO EM VOZ ALTA...</div></div><div class="equalizer"><i class="bar"></i><i class="bar"></i><i class="bar"></i><i class="bar"></i><i class="bar"></i><i class="bar"></i></div></div></div><audio id="audio" preload="auto" playsinline></audio><script>
const params=new URLSearchParams(location.search);const CLIENT_ID=(params.get('client')||'default').trim().slice(0,200)||'default';let lastId=0,processing=false,queue=[],seen=new Set();const card=document.getElementById('card'),avatar=document.getElementById('avatar'),text=document.getElementById('text'),user=document.getElementById('user'),audio=document.getElementById('audio');const sleep=ms=>new Promise(r=>setTimeout(r,ms));function log(stage,item,extra=''){console.log(`[TTS-OVERLAY] ${stage} Client=${CLIENT_ID} EventId=${item?.id??'-'} User=${item?.username??'-'} File=${item?.audioFileName??'-'} ${extra}`)}function escapeHtml(v){return String(v??'').replace(/[&<>\"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','\"':'&quot;',"'":'&#39;'}[c]))}async function ack(id,stage,error){try{await fetch('/api/tts/'+id+'/ack?client='+encodeURIComponent(CLIENT_ID),{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({stage,error}),cache:'no-store'})}catch(e){console.warn(`[TTS-OVERLAY] ACK_FAILED Client=${CLIENT_ID} EventId=${id} Stage=${stage}`,e)}}function show(item){text.innerHTML='<span class="quote">“</span>'+escapeHtml(item.text)+'<span class="quote">”</span>';user.innerHTML='Mensagem de <strong>'+escapeHtml(item.username)+'</strong>';avatar.src=item.avatarUrl||'';audio.pause();audio.currentTime=0;audio.src='/api/tts/audio/'+encodeURIComponent(item.audioFileName)+'?v='+encodeURIComponent(item.id);audio.load();card.className='card show';log('DISPLAYING',item)}async function waitForAudio(){if(audio.readyState>=3)return true;return await new Promise(resolve=>{let done=false;const finish=ok=>{if(done)return;done=true;audio.removeEventListener('canplay',onReady);audio.removeEventListener('canplaythrough',onReady);audio.removeEventListener('error',onError);resolve(ok)};const onReady=()=>finish(true),onError=()=>finish(false);audio.addEventListener('canplay',onReady);audio.addEventListener('canplaythrough',onReady);audio.addEventListener('error',onError);setTimeout(()=>finish(false),8000)})}async function play(item){processing=true;log('EVENT_RECEIVED',item);show(item);let played=false;try{if(!await waitForAudio()){log('AUDIO_LOAD_ERROR',item);await ack(item.id,'PLAY_ERROR','Áudio não carregou em até 8 segundos.');return}log('AUDIO_READY',item);await audio.play();played=true;log('PLAY_STARTED',item);await ack(item.id,'PLAY_STARTED');await new Promise(resolve=>{const finish=()=>{audio.removeEventListener('ended',finish);resolve()};audio.addEventListener('ended',finish);setTimeout(finish,Math.max(10000,(audio.duration||10)*1000+2000))});log('PLAY_FINISHED',item);await ack(item.id,'PLAY_FINISHED')}catch(error){console.error(`[TTS-OVERLAY] PLAY_ERROR Client=${CLIENT_ID} EventId=${item.id}`,error);await ack(item.id,'PLAY_ERROR',String(error))}if(played)lastId=Math.max(lastId,item.id);card.className='card hide';await sleep(380);card.className='card';processing=false;processQueue()}function processQueue(){if(processing||!queue.length)return;play(queue.shift())}async function initialize(){try{const response=await fetch('/api/tts/current?client='+encodeURIComponent(CLIENT_ID)+'&ts='+Date.now(),{cache:'no-store'});if(response.ok){const current=await response.json();lastId=Number(current.lastEventId||0);console.log(`[TTS-OVERLAY] INITIALIZED Client=${CLIENT_ID} LastId=${lastId} CurrentId=${current.currentId}`)}}catch(error){console.warn('[TTS-OVERLAY] INITIALIZE_ERROR',error)}poll()}async function poll(){try{const response=await fetch('/api/tts?client='+encodeURIComponent(CLIENT_ID)+'&ts='+Date.now(),{cache:'no-store'});if(response.ok){const items=await response.json();for(const item of items){if(item.id<=lastId||seen.has(item.id))continue;seen.add(item.id);queue.push(item);log('QUEUED',item)}processQueue()}}catch(error){console.warn('[TTS-OVERLAY] POLL_ERROR',error)}finally{setTimeout(poll,1000)}}initialize();
</script></body></html>
""";

public sealed record TtsOverlayPublishRequest(string Username, string? AvatarUrl, string Text, string AudioFileName);
public sealed record TtsOverlayAckRequest(string? Stage, string? Error);
}
