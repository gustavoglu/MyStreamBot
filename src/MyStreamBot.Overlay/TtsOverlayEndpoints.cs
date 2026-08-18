using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace MyStreamBot.Overlay;

public static class TtsOverlayEndpoints
{
    private static readonly ConcurrentQueue<TtsOverlayEvent> Events = new();
    private static readonly ConcurrentDictionary<long, TtsOverlayAck> Acks = new();
    private static long _nextId;

    public static void MapTtsOverlay(this WebApplication app)
    {
        var logger = app.Logger;

        app.MapPost("/api/tts", (TtsOverlayPublishRequest request) =>
        {
            if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Text) || string.IsNullOrWhiteSpace(request.AudioFileName))
                return Results.BadRequest(new { error = "username, text e audioFileName são obrigatórios." });

            var safeFileName = Path.GetFileName(request.AudioFileName);
            if (!string.Equals(safeFileName, request.AudioFileName, StringComparison.Ordinal))
                return Results.BadRequest(new { error = "Nome de arquivo inválido." });

            var audioPath = Path.Combine(GetAudioDirectory(), safeFileName);
            if (!File.Exists(audioPath))
            {
                logger.LogWarning("[TTS-OVERLAY] PUBLISH_REJECTED AudioFileMissing User={Username} File={FileName}", request.Username, safeFileName);
                return Results.NotFound(new { error = "Áudio ainda não está disponível.", fileName = safeFileName });
            }

            var id = Interlocked.Increment(ref _nextId);
            var item = new TtsOverlayEvent(id, request.Username.Trim(), request.AvatarUrl, request.Text.Trim(), safeFileName, DateTime.UtcNow);
            Events.Enqueue(item);
            TrimEvents();

            logger.LogInformation("[TTS-OVERLAY] PUBLISHED EventId={EventId} User={Username} File={FileName} Text={Text}", id, item.Username, item.AudioFileName, item.Text);
            return Results.Ok(new { id });
        });

        app.MapGet("/api/tts/current", () => Results.Ok(new { id = Volatile.Read(ref _nextId) }));

        app.MapGet("/api/tts", (long? afterId) =>
        {
            var lastId = afterId ?? 0;
            var items = Events.Where(x => x.Id > lastId).OrderBy(x => x.Id).Take(20).ToArray();
            if (items.Length > 0)
                logger.LogInformation("[TTS-OVERLAY] POLL_RETURNED AfterId={AfterId} Count={Count} FirstId={FirstId} LastId={LastId}", lastId, items.Length, items[0].Id, items[^1].Id);
            return Results.Ok(items);
        });

        // ACK enviado pelo Browser Source/OBS para informar que o evento chegou ao overlay.
        app.MapPost("/api/tts/{id:long}/ack", (long id, TtsOverlayAckRequest request) =>
        {
            if (!Events.Any(x => x.Id == id))
                return Results.NotFound(new { error = "Evento TTS não encontrado.", id });

            var ack = new TtsOverlayAck(id, request.Stage ?? "UNKNOWN", DateTime.UtcNow, request.Error);
            Acks[id] = ack;

            if (string.Equals(request.Stage, "PLAY_STARTED", StringComparison.OrdinalIgnoreCase))
                logger.LogInformation("[TTS-OVERLAY] SHOWN EventId={EventId} Stage=PLAY_STARTED", id);
            else if (string.Equals(request.Stage, "PLAY_FINISHED", StringComparison.OrdinalIgnoreCase))
                logger.LogInformation("[TTS-OVERLAY] PLAY_FINISHED EventId={EventId}", id);
            else if (string.Equals(request.Stage, "PLAY_ERROR", StringComparison.OrdinalIgnoreCase))
                logger.LogError("[TTS-OVERLAY] PLAY_ERROR EventId={EventId} Error={Error}", id, request.Error ?? "unknown");
            else
                logger.LogInformation("[TTS-OVERLAY] ACK EventId={EventId} Stage={Stage}", id, request.Stage ?? "UNKNOWN");

            return Results.Ok(new { id, stage = request.Stage });
        });

        app.MapGet("/api/tts/acks", () => Results.Ok(Acks.Values.OrderByDescending(x => x.AckedAtUtc).Take(50).ToArray()));

        app.MapPost("/api/tts/repeat-last", () =>
        {
            var item = Events.OrderByDescending(x => x.Id).FirstOrDefault();
            return item is null ? Results.NotFound(new { error = "Nenhum TTS disponível para repetir." }) : Results.Ok(EnqueueRepeat(item, logger));
        });

        app.MapPost("/api/tts/repeat-user/{username}", (string username) =>
        {
            var item = Events.Where(x => !x.IsRepeat && string.Equals(x.Username, username, StringComparison.OrdinalIgnoreCase)).OrderByDescending(x => x.Id).FirstOrDefault();
            return item is null ? Results.NotFound(new { error = "Nenhum TTS encontrado para este viewer.", username }) : Results.Ok(EnqueueRepeat(item, logger));
        });

        app.MapGet("/api/tts/audio/{fileName}", (string fileName) =>
        {
            var safeFileName = Path.GetFileName(fileName);
            if (!string.Equals(safeFileName, fileName, StringComparison.Ordinal)) return Results.BadRequest();
            var path = Path.Combine(GetAudioDirectory(), safeFileName);
            if (!File.Exists(path))
            {
                logger.LogWarning("[TTS-OVERLAY] AUDIO_NOT_FOUND File={FileName}", safeFileName);
                return Results.NotFound();
            }
            logger.LogDebug("[TTS-OVERLAY] AUDIO_REQUESTED File={FileName}", safeFileName);
            return Results.File(path, "audio/mpeg", enableRangeProcessing: true);
        });

        app.MapGet("/tts", () => Results.Content(TtsHtml(), "text/html; charset=utf-8"));
    }

    private static object EnqueueRepeat(TtsOverlayEvent original, ILogger logger)
    {
        var id = Interlocked.Increment(ref _nextId);
        var repeated = original with { Id = id, CreatedAtUtc = DateTime.UtcNow, IsRepeat = true };
        Events.Enqueue(repeated);
        TrimEvents();
        logger.LogInformation("[TTS-OVERLAY] REPEAT_QUEUED EventId={EventId} OriginalUser={Username} File={FileName}", id, repeated.Username, repeated.AudioFileName);
        return new { id, username = repeated.Username, text = repeated.Text, audioFileName = repeated.AudioFileName };
    }

    private static void TrimEvents()
    {
        while (Events.Count > 50 && Events.TryDequeue(out _)) { }
        while (Acks.Count > 100 && Acks.Keys.OrderBy(x => x).FirstOrDefault() is var oldest && oldest > 0) Acks.TryRemove(oldest, out _);
    }

    private static string GetAudioDirectory()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyStreamBot", "tts-test");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string TtsHtml() => """
<!doctype html><html lang="pt-BR"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>MyStreamBot - Voz do Chat</title><style>
:root{color-scheme:dark}*{box-sizing:border-box}html,body{margin:0;padding:0;background:transparent;font-family:Arial,Helvetica,sans-serif;color:#fff}body{width:900px;height:260px;overflow:hidden}.wrap{width:100%;height:100%;display:flex;align-items:center;justify-content:center}.card{width:820px;min-height:150px;padding:18px 26px;position:relative;border:1px solid rgba(255,193,52,.78);border-radius:22px;background:rgba(15,15,17,.72);box-shadow:0 0 28px rgba(255,176,20,.25),inset 0 0 35px rgba(255,176,20,.05);backdrop-filter:blur(8px);opacity:0;transform:translateX(-70px) scale(.98);pointer-events:none}.card.show{animation:enter .45s cubic-bezier(.2,.8,.25,1) forwards}.card.hide{animation:exit .35s ease forwards}.badge{position:absolute;top:-16px;left:50%;transform:translateX(-50%);padding:7px 22px;border:1px solid #ffbd2e;border-radius:999px;background:#111;color:#ffbd2e;font-weight:900;font-size:15px;letter-spacing:.5px;white-space:nowrap}.avatar{position:absolute;left:22px;top:50%;transform:translateY(-50%);width:54px;height:54px;border-radius:50%;object-fit:cover;border:2px solid rgba(255,193,47,.75);background:rgba(255,193,47,.08)}.speaker{position:absolute;left:68px;top:50%;transform:translateY(-50%);font-size:20px;color:#ffc12f}.content{margin-left:105px;margin-right:115px}.text{font-size:31px;line-height:1.12;font-weight:850;word-break:break-word;text-shadow:0 0 15px rgba(255,193,47,.18)}.text .quote{color:#ffc12f}.user{margin-top:9px;color:#aeb5c2;font-size:16px}.user strong{color:#ffc12f}.equalizer{position:absolute;right:24px;top:50%;transform:translateY(-50%);display:flex;gap:4px;align-items:flex-end;height:48px}.bar{width:5px;background:#ffc12f;border-radius:5px;animation:equalize .7s ease-in-out infinite alternate}.bar:nth-child(1){height:15px;animation-delay:-.5s}.bar:nth-child(2){height:31px;animation-delay:-.2s}.bar:nth-child(3){height:44px;animation-delay:-.7s}.bar:nth-child(4){height:25px;animation-delay:-.1s}.bar:nth-child(5){height:38px;animation-delay:-.4s}.bar:nth-child(6){height:19px;animation-delay:-.6s}.status{margin-top:12px;color:#ffc12f;font-size:11px;font-weight:800;letter-spacing:1.3px;text-transform:uppercase}@keyframes enter{from{opacity:0;transform:translateX(-70px) scale(.98)}to{opacity:1;transform:translateX(0) scale(1)}}@keyframes exit{from{opacity:1;transform:translateX(0)}to{opacity:0;transform:translateX(40px)}}@keyframes equalize{to{transform:scaleY(.35)}}
</style></head><body><div class="wrap"><div id="card" class="card"><div class="badge">🔊 O CHAT MANDOU!</div><img id="avatar" class="avatar" alt=""><div class="speaker">🔊</div><div class="content"><div id="text" class="text"></div><div id="user" class="user"></div><div class="status">LENDO EM VOZ ALTA...</div></div><div class="equalizer"><i class="bar"></i><i class="bar"></i><i class="bar"></i><i class="bar"></i><i class="bar"></i><i class="bar"></i></div></div></div><audio id="audio" preload="auto" playsinline></audio><script>
const STORAGE_KEY='mystreambot.tts.lastPlayedId';const fallbackAvatar='data:image/svg+xml;charset=UTF-8,'+encodeURIComponent('<svg xmlns="http://www.w3.org/2000/svg" width="80" height="80"><rect width="100%" height="100%" fill="#20242c"/><circle cx="40" cy="31" r="14" fill="#9aa3b2"/><path d="M15 72c3-17 47-17 50 0" fill="#9aa3b2"/></svg>');let lastId=0,processing=false,queue=[],seen=new Set();const card=document.getElementById('card'),avatar=document.getElementById('avatar'),text=document.getElementById('text'),user=document.getElementById('user'),audio=document.getElementById('audio');const sleep=ms=>new Promise(r=>setTimeout(r,ms));function log(stage,item,extra=''){console.log(`[TTS-OVERLAY] ${stage} EventId=${item?.id??'-'} User=${item?.username??'-'} File=${item?.audioFileName??'-'} ${extra}`)}function escapeHtml(v){return String(v??'').replace(/[&<>\"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','\"':'&quot;',"'":'&#39;'}[c]))}async function ack(id,stage,error){try{await fetch('/api/tts/'+encodeURIComponent(id)+'/ack',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({stage,error}),cache:'no-store'})}catch(e){console.warn(`[TTS-OVERLAY] ACK_FAILED EventId=${id} Stage=${stage}`,e)}}function show(item){text.innerHTML='<span class="quote">“</span>'+escapeHtml(item.text)+'<span class="quote">”</span>';user.innerHTML='Mensagem de <strong>'+escapeHtml(item.username)+'</strong>';avatar.src=item.avatarUrl||fallbackAvatar;avatar.onerror=()=>avatar.src=fallbackAvatar;audio.pause();audio.currentTime=0;audio.src='/api/tts/audio/'+encodeURIComponent(item.audioFileName)+'?v='+encodeURIComponent(item.id);audio.load();card.className='card show';log('DISPLAYING',item)}async function waitForAudio(item){if(audio.readyState>=3)return true;return await new Promise(resolve=>{let done=false;const finish=ok=>{if(done)return;done=true;audio.removeEventListener('canplay',onReady);audio.removeEventListener('canplaythrough',onReady);audio.removeEventListener('error',onError);resolve(ok)};const onReady=()=>finish(true);const onError=()=>finish(false);audio.addEventListener('canplay',onReady);audio.addEventListener('canplaythrough',onReady);audio.addEventListener('error',onError);setTimeout(()=>finish(false),8000)})}async function play(item){processing=true;log('EVENT_RECEIVED',item);show(item);let played=false;try{const ready=await waitForAudio(item);if(!ready){log('AUDIO_LOAD_ERROR',item);await ack(item.id,'PLAY_ERROR','Áudio não carregou em até 8 segundos.');return}log('AUDIO_READY',item);await audio.play();played=true;log('PLAY_STARTED',item);await ack(item.id,'PLAY_STARTED');await new Promise(resolve=>{const finish=()=>{audio.removeEventListener('ended',finish);resolve()};audio.addEventListener('ended',finish);setTimeout(finish,Math.max(10000,(audio.duration||10)*1000+2000))});log('PLAY_FINISHED',item);await ack(item.id,'PLAY_FINISHED')}catch(error){console.error(`[TTS-OVERLAY] PLAY_ERROR EventId=${item.id}`,error);await ack(item.id,'PLAY_ERROR',String(error))}if(played){try{localStorage.setItem(STORAGE_KEY,String(item.id))}catch{}lastId=Math.max(lastId,item.id)}card.className='card hide';await sleep(380);card.className='card';processing=false;processQueue()}function processQueue(){if(processing||!queue.length)return;play(queue.shift())}async function initialize(){try{const saved=Number.parseInt(localStorage.getItem(STORAGE_KEY)||'',10);if(Number.isFinite(saved))lastId=Math.max(0,saved);else{const response=await fetch('/api/tts/current?ts='+Date.now(),{cache:'no-store'});if(response.ok){const current=await response.json();lastId=Number(current.id||0);localStorage.setItem(STORAGE_KEY,String(lastId))}}console.log(`[TTS-OVERLAY] INITIALIZED LastId=${lastId}`)}catch(error){console.warn('[TTS-OVERLAY] INITIALIZE_ERROR',error)}poll()}async function poll(){try{const response=await fetch('/api/tts?afterId='+lastId+'&ts='+Date.now(),{cache:'no-store'});if(response.ok){const items=await response.json();for(const item of items){if(item.id<=lastId||seen.has(item.id))continue;seen.add(item.id);queue.push(item);log('QUEUED',item)}processQueue()}}catch(error){console.warn('[TTS-OVERLAY] POLL_ERROR',error)}finally{setTimeout(poll,1000)}}initialize();
</script></body></html>
""";

public sealed record TtsOverlayPublishRequest(string Username, string? AvatarUrl, string Text, string AudioFileName);
public sealed record TtsOverlayEvent(long Id, string Username, string? AvatarUrl, string Text, string AudioFileName, DateTime CreatedAtUtc, bool IsRepeat = false);
public sealed record TtsOverlayAckRequest(string? Stage, string? Error);
public sealed record TtsOverlayAck(long EventId, string Stage, DateTime AckedAtUtc, string? Error);
}
