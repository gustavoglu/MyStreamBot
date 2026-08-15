using System.Collections.Concurrent;
using System.Text.Json;

namespace MyStreamBot.Overlay;

public static class TtsOverlayEndpoints
{
    private static readonly ConcurrentQueue<TtsOverlayEvent> Events = new();
    private static long _nextId;

    public static void MapTtsOverlay(this WebApplication app)
    {
        app.MapPost("/api/tts", async (TtsOverlayPublishRequest request, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Username) ||
                string.IsNullOrWhiteSpace(request.Text) ||
                string.IsNullOrWhiteSpace(request.AudioFileName))
            {
                return Results.BadRequest(new { error = "username, text e audioFileName são obrigatórios." });
            }

            var safeFileName = Path.GetFileName(request.AudioFileName);
            if (!string.Equals(safeFileName, request.AudioFileName, StringComparison.Ordinal))
                return Results.BadRequest(new { error = "Nome de arquivo inválido." });

            var audioDirectory = GetAudioDirectory();
            var audioPath = Path.Combine(audioDirectory, safeFileName);

            if (!File.Exists(audioPath))
                return Results.NotFound(new { error = "Áudio ainda não está disponível.", fileName = safeFileName });

            var id = Interlocked.Increment(ref _nextId);
            Events.Enqueue(new TtsOverlayEvent(id, request.Username.Trim(), request.Text.Trim(), safeFileName, DateTime.UtcNow));

            while (Events.Count > 50 && Events.TryDequeue(out _))
            {
            }

            return Results.Ok(new { id });
        });

        app.MapGet("/api/tts", (long? afterId) =>
        {
            var lastId = afterId ?? 0;
            var items = Events
                .Where(x => x.Id > lastId)
                .OrderBy(x => x.Id)
                .Take(20)
                .ToArray();

            return Results.Ok(items);
        });

        app.MapGet("/api/tts/audio/{fileName}", (string fileName) =>
        {
            var safeFileName = Path.GetFileName(fileName);
            if (!string.Equals(safeFileName, fileName, StringComparison.Ordinal))
                return Results.BadRequest();

            var path = Path.Combine(GetAudioDirectory(), safeFileName);
            if (!File.Exists(path))
                return Results.NotFound();

            return Results.File(path, "audio/mpeg", enableRangeProcessing: true);
        });

        app.MapGet("/tts", () => Results.Content(TtsHtml(), "text/html; charset=utf-8"));
    }

    private static string GetAudioDirectory()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MyStreamBot",
            "tts-test");

        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string TtsHtml() => """
<!doctype html>
<html lang="pt-BR">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>MyStreamBot - Voz do Chat</title>
<style>
:root{color-scheme:dark}*{box-sizing:border-box}html,body{margin:0;padding:0;background:transparent;font-family:Arial,Helvetica,sans-serif;color:#fff}body{width:900px;height:260px;overflow:hidden}.wrap{width:100%;height:100%;display:flex;align-items:center;justify-content:center}.card{width:820px;min-height:150px;padding:18px 26px;position:relative;border:1px solid rgba(255,193,52,.78);border-radius:22px;background:rgba(15,15,17,.72);box-shadow:0 0 28px rgba(255,176,20,.25),inset 0 0 35px rgba(255,176,20,.05);backdrop-filter:blur(8px);opacity:0;transform:translateX(-70px) scale(.98);pointer-events:none}.card.show{animation:enter .45s cubic-bezier(.2,.8,.25,1) forwards}.card.hide{animation:exit .35s ease forwards}.badge{position:absolute;top:-16px;left:50%;transform:translateX(-50%);padding:7px 22px;border:1px solid #ffbd2e;border-radius:999px;background:#111;color:#ffbd2e;font-weight:900;font-size:15px;letter-spacing:.5px;white-space:nowrap}.speaker{position:absolute;left:22px;top:50%;transform:translateY(-50%);font-size:42px;color:#ffc12f}.content{margin-left:72px;margin-right:115px}.text{font-size:31px;line-height:1.12;font-weight:850;word-break:break-word;text-shadow:0 0 15px rgba(255,193,47,.18)}.text .quote{color:#ffc12f}.user{margin-top:9px;color:#aeb5c2;font-size:16px}.user strong{color:#ffc12f}.equalizer{position:absolute;right:24px;top:50%;transform:translateY(-50%);display:flex;gap:4px;align-items:flex-end;height:48px}.bar{width:5px;background:#ffc12f;border-radius:5px;animation:equalize .7s ease-in-out infinite alternate}.bar:nth-child(1){height:15px;animation-delay:-.5s}.bar:nth-child(2){height:31px;animation-delay:-.2s}.bar:nth-child(3){height:44px;animation-delay:-.7s}.bar:nth-child(4){height:25px;animation-delay:-.1s}.bar:nth-child(5){height:38px;animation-delay:-.4s}.bar:nth-child(6){height:19px;animation-delay:-.6s}.status{margin-top:12px;color:#ffc12f;font-size:11px;font-weight:800;letter-spacing:1.3px;text-transform:uppercase}@keyframes enter{from{opacity:0;transform:translateX(-70px) scale(.98)}to{opacity:1;transform:translateX(0) scale(1)}}@keyframes exit{from{opacity:1;transform:translateX(0)}to{opacity:0;transform:translateX(40px)}}@keyframes equalize{to{transform:scaleY(.35)}}
</style>
</head>
<body>
<div class="wrap"><div id="card" class="card"><div class="badge">🔊 O CHAT MANDOU!</div><div class="speaker">🔈</div><div class="content"><div id="text" class="text"></div><div id="user" class="user"></div><div class="status">LENDO EM VOZ ALTA...</div></div><div class="equalizer"><i class="bar"></i><i class="bar"></i><i class="bar"></i><i class="bar"></i><i class="bar"></i><i class="bar"></i></div></div></div>
<audio id="audio" preload="auto"></audio>
<script>
let lastId=0,processing=false,queue=[],seen=new Set();const card=document.getElementById('card'),text=document.getElementById('text'),user=document.getElementById('user'),audio=document.getElementById('audio');
const sleep=ms=>new Promise(r=>setTimeout(r,ms));
function escapeHtml(v){return String(v??'').replace(/[&<>\"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','\"':'&quot;',"'":'&#39;'}[c]))}
function show(item){text.innerHTML='<span class="quote">“</span>'+escapeHtml(item.text)+'<span class="quote">”</span>';user.innerHTML='Mensagem de <strong>'+escapeHtml(item.username)+'</strong>';audio.src='/api/tts/audio/'+encodeURIComponent(item.audioFileName);card.className='card show';}
async function play(item){processing=true;show(item);try{await audio.play();await new Promise(resolve=>{audio.onended=resolve;audio.onerror=resolve})}catch{await sleep(Math.max(1800,Math.min(8000,(item.text||'').length*65)))}card.className='card hide';await sleep(380);card.className='card';processing=false;processQueue()}
function processQueue(){if(processing||!queue.length)return;const item=queue.shift();play(item)}
async function poll(){try{const response=await fetch('/api/tts?afterId='+lastId+'&ts='+Date.now(),{cache:'no-store'});if(response.ok){const items=await response.json();for(const item of items){lastId=Math.max(lastId,item.id);if(!seen.has(item.id)){seen.add(item.id;queue.push(item)}}processQueue()}}catch{}finally{setTimeout(poll,700)}}poll();
</script>
</body>
</html>
""";

public sealed record TtsOverlayPublishRequest(string Username, string Text, string AudioFileName);
public sealed record TtsOverlayEvent(long Id, string Username, string Text, string AudioFileName, DateTime CreatedAtUtc);
