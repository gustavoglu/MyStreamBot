using Microsoft.EntityFrameworkCore;
using MyStreamBot.Core.Enums;
using MyStreamBot.Infrastructure.Persistence;

namespace MyStreamBot.Overlay;

public static class TtsAdminEndpoints
{
    public static void MapTtsAdmin(this WebApplication app)
    {
        app.MapGet("/admin", () => Results.Content(AdminHtml(), "text/html; charset=utf-8"));
        app.MapGet("/api/admin/tts/status", async (IDbContextFactory<MyStreamBotDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var settings = await GetSettingsAsync(db, ct);
            return Results.Ok(new { settings.AcceptCommands, settings.PublishEnabled, blocked = await db.TtsBlockedViewers.AsNoTracking().OrderBy(x => x.Username).ToListAsync(ct) });
        });
        app.MapPost("/api/admin/tts/settings", async (AdminSettingsRequest request, IDbContextFactory<MyStreamBotDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var settings = await GetSettingsAsync(db, ct); settings.AcceptCommands = request.AcceptCommands; settings.PublishEnabled = request.PublishEnabled; settings.UpdatedAtUtc = DateTime.UtcNow; await db.SaveChangesAsync(ct); return Results.Ok(settings);
        });
        app.MapPost("/api/admin/tts/block", async (BlockViewerRequest request, IDbContextFactory<MyStreamBotDbContext> factory, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.PlatformUserId) || string.IsNullOrWhiteSpace(request.Username)) return Results.BadRequest();
            await using var db = await factory.CreateDbContextAsync(ct); var platform = request.Platform?.Trim() ?? "Unknown";
            var item = await db.TtsBlockedViewers.FirstOrDefaultAsync(x => x.Platform == platform && x.PlatformUserId == request.PlatformUserId.Trim(), ct);
            if (item is null) db.TtsBlockedViewers.Add(new() { Platform = platform, PlatformUserId = request.PlatformUserId.Trim(), Username = request.Username.Trim() }); else item.Username = request.Username.Trim();
            await db.SaveChangesAsync(ct); return Results.Ok();
        });
        app.MapDelete("/api/admin/tts/block", async (string platform, string platformUserId, IDbContextFactory<MyStreamBotDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct); var item = await db.TtsBlockedViewers.FirstOrDefaultAsync(x => x.Platform == platform && x.PlatformUserId == platformUserId, ct); if (item is null) return Results.NotFound(); db.TtsBlockedViewers.Remove(item); await db.SaveChangesAsync(ct); return Results.Ok();
        });
        app.MapGet("/api/admin/tts/history", async (int? take, IDbContextFactory<MyStreamBotDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct); return Results.Ok(await db.TtsOverlayEvents.AsNoTracking().OrderByDescending(x => x.Id).Take(Math.Clamp(take ?? 100, 1, 500)).ToListAsync(ct));
        });
        app.MapPost("/api/admin/tts/{id:long}/repeat", async (long id, IDbContextFactory<MyStreamBotDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct); var original = await db.TtsOverlayEvents.FirstOrDefaultAsync(x => x.Id == id, ct); if (original is null) return Results.NotFound();
            var repeat = new MyStreamBot.Core.Entities.TtsOverlayEvent { Username = original.Username, AvatarUrl = original.AvatarUrl, Text = original.Text, AudioFileName = original.AudioFileName, CreatedAtUtc = DateTime.UtcNow, IsRepeat = true }; db.TtsOverlayEvents.Add(repeat); await db.SaveChangesAsync(ct); return Results.Ok(new { repeat.Id });
        });
        app.MapDelete("/api/admin/tts/{id:long}", async (long id, IDbContextFactory<MyStreamBotDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct); var item = await db.TtsOverlayEvents.FirstOrDefaultAsync(x => x.Id == id, ct); if (item is null) return Results.NotFound(); db.TtsOverlayEvents.Remove(item); await db.SaveChangesAsync(ct); return Results.Ok();
        });
        app.MapGet("/api/admin/users", async (string? q, IDbContextFactory<MyStreamBotDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct); var query = db.Users.AsNoTracking().AsQueryable(); if (!string.IsNullOrWhiteSpace(q)) query = query.Where(x => x.Username.Contains(q.Trim()));
            return Results.Ok(await query.OrderByDescending(x => x.Points).Take(100).Select(x => new { x.Id, x.Username, x.AvatarUrl, x.Points, x.Platform, x.PlatformUserId }).ToListAsync(ct));
        });
        app.MapPost("/api/admin/users/{id:long}/points", async (long id, PointsRequest request, IDbContextFactory<MyStreamBotDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct); var user = await db.Users.FirstOrDefaultAsync(x => x.Id == id, ct); if (user is null) return Results.NotFound();
            checked { user.Points += request.Amount; } db.PointTransactions.Add(new MyStreamBot.Core.Entities.PointTransaction { UserId = user.Id, Amount = request.Amount, Type = request.Amount >= 0 ? PointTransactionType.Admin : PointTransactionType.Penalty, Description = request.Reason ?? "Alteração administrativa", CreatedAtUtc = DateTime.UtcNow, SourceMessageKey = $"admin:{Guid.NewGuid():N}" }); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return Results.Ok(new { user.Id, user.Username, user.Points });
        });
    }

    private static async Task<MyStreamBot.Core.Entities.TtsAdminSettings> GetSettingsAsync(MyStreamBotDbContext db, CancellationToken ct)
    {
        var settings = await db.TtsAdminSettings.FirstOrDefaultAsync(x => x.Id == 1, ct); if (settings is not null) return settings; settings = new() { Id = 1 }; db.TtsAdminSettings.Add(settings); await db.SaveChangesAsync(ct); return settings;
    }
    private sealed record AdminSettingsRequest(bool AcceptCommands, bool PublishEnabled);
    private sealed record BlockViewerRequest(string Platform, string PlatformUserId, string Username);
    private sealed record PointsRequest(long Amount, string? Reason);

    private static string AdminHtml() => """
<!doctype html><html lang="pt-BR"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>MyStreamBot Admin</title><style>*{box-sizing:border-box}body{margin:0;background:#090b10;color:#eee;font-family:Arial,sans-serif}header{padding:22px 28px;border-bottom:1px solid #252b36;position:sticky;top:0;background:#090b10ee;backdrop-filter:blur(8px);z-index:2}h1{margin:0;font-size:24px}main{max-width:1200px;margin:auto;padding:22px;display:grid;gap:16px}.grid{display:grid;grid-template-columns:1fr 1fr;gap:16px}.card{background:#121620;border:1px solid #252c39;border-radius:15px;padding:18px}.card h2{font-size:16px;margin:0 0 14px}.muted{color:#8f99aa;font-size:12px}.controls{display:flex;gap:9px;flex-wrap:wrap}.btn{border:0;border-radius:9px;padding:10px 14px;background:#28303d;color:white;font-weight:800;cursor:pointer}.green{background:#176b42}.red{background:#7b222a}.orange{background:#825b13}.status{font-size:18px;font-weight:900;margin-bottom:12px}.row{display:flex;align-items:center;gap:10px;padding:10px 0;border-bottom:1px solid #252c39}.grow{flex:1;min-width:0}.name{font-weight:800}.small{font-size:11px;color:#9da7b7;margin-top:3px;overflow:hidden;text-overflow:ellipsis}.search{width:100%;padding:10px;border-radius:8px;border:1px solid #303846;background:#0c1017;color:#fff;margin-bottom:8px}.tools{display:flex;gap:6px}.tools input{width:90px;padding:8px;border-radius:7px;border:1px solid #303846;background:#0c1017;color:#fff}.table{width:100%;border-collapse:collapse}.table th,.table td{padding:9px;border-bottom:1px solid #252c39;text-align:left;font-size:12px}.table th{color:#9da7b7;text-transform:uppercase;font-size:10px}.toast{position:fixed;right:20px;bottom:20px;background:#202a38;padding:12px 16px;border-radius:9px;display:none}@media(max-width:800px){.grid{grid-template-columns:1fr}}
</style></head><body><header><h1>🎛️ MyStreamBot — TTS Admin</h1><div class="muted">Controle de voz, viewers, pontos e fila do overlay</div></header><main>
<div class="grid"><section class="card"><h2>🔊 Controle geral</h2><div id="status" class="status">Carregando...</div><div class="controls"><button id="commands" class="btn" onclick="toggleCommands()"></button><button id="publish" class="btn" onclick="togglePublish()"></button></div><p class="muted">Bloquear !voz rejeita novos comandos sem gastar pontos. Pausar o overlay mantém a fila parada até você retomar.</p></section><section class="card"><h2>⚡ Ações rápidas</h2><div class="controls"><button class="btn orange" onclick="repeatLast()">▶ Repetir último</button><button class="btn red" onclick="clearQueue()">🗑 Limpar fila</button></div></section></div>
<section class="card"><h2>🚫 Viewers bloqueados</h2><div id="blocked">Carregando...</div></section>
<section class="card"><h2>👥 Pontos / viewers</h2><input id="search" class="search" placeholder="Pesquisar viewer..." oninput="loadUsers()"><div id="users"></div></section>
<section class="card"><h2>🔊 Histórico de !voz</h2><div class="muted" style="margin-bottom:10px">▶ repete imediatamente no overlay • 🗑 remove uma pendência</div><div style="overflow:auto"><table class="table"><thead><tr><th>Hora</th><th>Viewer</th><th>Texto</th><th>Ações</th></tr></thead><tbody id="history"></tbody></table></div></section>
</main><div id="toast" class="toast"></div><script>
let state={AcceptCommands:true,PublishEnabled:true};const $=x=>document.getElementById(x);const esc=x=>String(x??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));const fmt=x=>Number(x||0).toLocaleString('pt-BR');function toast(x){$('toast').textContent=x;$('toast').style.display='block';setTimeout(()=>$('toast').style.display='none',2000)}async function api(url,opt={}){const r=await fetch(url,{headers:{'Content-Type':'application/json'},...opt});if(!r.ok)throw Error('HTTP '+r.status);return r.json()}function render(){ $('status').textContent=state.AcceptCommands?'🟢 !voz ATIVO':'🔴 !voz BLOQUEADO';$('commands').textContent=state.AcceptCommands?'🔴 Bloquear !voz':'🟢 Liberar !voz';$('commands').className='btn '+(state.AcceptCommands?'red':'green');$('publish').textContent=state.PublishEnabled?'⏸ Pausar overlay':'▶ Retomar overlay';$('publish').className='btn '+(state.PublishEnabled?'orange':'green') }async function load(){try{const x=await api('/api/admin/tts/status');state=x;render();$('blocked').innerHTML=x.blocked.length?x.blocked.map(b=>`<div class="row"><div class="grow"><div class="name">${esc(b.username)}</div><div class="small">${esc(b.platform)} • ${esc(b.platformUserId)}</div></div><button class="btn" onclick="unblock('${esc(b.platform)}','${esc(b.platformUserId)}')">Desbloquear</button></div>`).join(''):'<div class="muted">Nenhum viewer bloqueado.</div>';await loadUsers();await loadHistory()}catch(e){toast(e.message)}}async function save(){state=await api('/api/admin/tts/settings',{method:'POST',body:JSON.stringify(state)});render();toast('Configuração salva')}async function toggleCommands(){state.AcceptCommands=!state.AcceptCommands;await save()}async function togglePublish(){state.PublishEnabled=!state.PublishEnabled;await save()}async function unblock(p,id){await api('/api/admin/tts/block?platform='+encodeURIComponent(p)+'&platformUserId='+encodeURIComponent(id),{method:'DELETE'});load()}async function block(p,id,name){await api('/api/admin/tts/block',{method:'POST',body:JSON.stringify({platform:p,platformUserId:id,username:name})});toast(name+' bloqueado');load()}async function loadUsers(){const q=encodeURIComponent($('search').value);const us=await api('/api/admin/users?q='+q);$('users').innerHTML=us.map(u=>`<div class="row"><div class="grow"><div class="name">${esc(u.username)}</div><div class="small">${esc(u.platform)} • saldo ${fmt(u.points)}</div></div><button class="btn red" onclick="block('${esc(u.platform)}','${esc(u.platformUserId)}','${esc(u.username)}')">🚫</button><div class="tools"><input id="p${u.id}" type="number" placeholder="±"><button class="btn" onclick="points(${u.id})">🪙</button></div></div>`).join('')||'<div class="muted">Nenhum viewer.</div>'}async function points(id){const amount=Number($('p'+id).value);if(!amount)return;await api('/api/admin/users/'+id+'/points',{method:'POST',body:JSON.stringify({amount,reason:'Alteração administrativa'})});toast('Pontos alterados');loadUsers()}async function loadHistory(){const xs=await api('/api/admin/tts/history?take=100');$('history').innerHTML=xs.map(x=>`<tr><td>${new Date(x.createdAtUtc).toLocaleTimeString('pt-BR')}</td><td>${esc(x.username)}${x.isRepeat?' <small>(repeat)</small>':''}</td><td>${esc(x.text)}</td><td><button class="btn" onclick="repeat(${x.id})">▶</button> <button class="btn red" onclick="removeEvent(${x.id})">🗑</button></td></tr>`).join('')||'<tr><td colspan="4" class="muted">Nenhum TTS.</td></tr>'}async function repeat(id){await api('/api/admin/tts/'+id+'/repeat',{method:'POST'});toast('Repetido');loadHistory()}async function removeEvent(id){await api('/api/admin/tts/'+id,{method:'DELETE'});toast('Removido');loadHistory()}async function repeatLast(){await api('/api/tts/repeat-last',{method:'POST'});toast('Último TTS repetido')}async function clearQueue(){if(!confirm('Remover os eventos atuais do overlay?'))return;const xs=await api('/api/admin/tts/history?take=500');for(const x of xs)await fetch('/api/admin/tts/'+x.id,{method:'DELETE'});toast('Fila limpa');loadHistory()}load();setInterval(load,5000);
</script></body></html>
""";
}
