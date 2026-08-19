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
            return Results.Ok(new
            {
                settings.AcceptCommands,
                settings.PublishEnabled,
                blocked = await db.TtsBlockedViewers.AsNoTracking().OrderBy(x => x.Username).ToListAsync(ct)
            });
        });

        app.MapPost("/api/admin/tts/settings", async (AdminSettingsRequest request, IDbContextFactory<MyStreamBotDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var settings = await GetSettingsAsync(db, ct);
            settings.AcceptCommands = request.AcceptCommands;
            settings.PublishEnabled = request.PublishEnabled;
            settings.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { settings.AcceptCommands, settings.PublishEnabled });
        });

        app.MapPost("/api/admin/tts/block", async (BlockViewerRequest request, IDbContextFactory<MyStreamBotDbContext> factory, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.PlatformUserId) || string.IsNullOrWhiteSpace(request.Username)) return Results.BadRequest();
            await using var db = await factory.CreateDbContextAsync(ct);
            var platform = request.Platform?.Trim() ?? "Unknown";
            var item = await db.TtsBlockedViewers.FirstOrDefaultAsync(x => x.Platform == platform && x.PlatformUserId == request.PlatformUserId.Trim(), ct);
            if (item is null)
                db.TtsBlockedViewers.Add(new() { Platform = platform, PlatformUserId = request.PlatformUserId.Trim(), Username = request.Username.Trim() });
            else
                item.Username = request.Username.Trim();
            await db.SaveChangesAsync(ct);
            return Results.Ok();
        });

        app.MapDelete("/api/admin/tts/block", async (string platform, string platformUserId, IDbContextFactory<MyStreamBotDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var item = await db.TtsBlockedViewers.FirstOrDefaultAsync(x => x.Platform == platform && x.PlatformUserId == platformUserId, ct);
            if (item is null) return Results.NotFound();
            db.TtsBlockedViewers.Remove(item);
            await db.SaveChangesAsync(ct);
            return Results.Ok();
        });

        app.MapGet("/api/admin/tts/history", async (int? page, int? pageSize, IDbContextFactory<MyStreamBotDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var size = Math.Clamp(pageSize ?? 20, 5, 100);
            var total = await db.TtsOverlayEvents.AsNoTracking().LongCountAsync(ct);
            var pages = Math.Max(1L, (total + size - 1) / size);
            var currentPage = (int)Math.Clamp(page ?? 1, 1, pages);
            var items = await db.TtsOverlayEvents.AsNoTracking()
                .OrderByDescending(x => x.Id)
                .Skip((currentPage - 1) * size)
                .Take(size)
                .ToListAsync(ct);
            return Results.Ok(new { items, page = currentPage, pageSize = size, total, pages });
        });

        app.MapPost("/api/admin/tts/{id:long}/repeat", async (long id, IDbContextFactory<MyStreamBotDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var original = await db.TtsOverlayEvents.FirstOrDefaultAsync(x => x.Id == id && !x.IsCanceled, ct);
            if (original is null) return Results.NotFound();
            var repeat = new MyStreamBot.Core.Entities.TtsOverlayEvent
            {
                Username = original.Username,
                AvatarUrl = original.AvatarUrl,
                Text = original.Text,
                AudioFileName = original.AudioFileName,
                CreatedAtUtc = DateTime.UtcNow,
                IsRepeat = true,
                IsCanceled = false
            };
            db.TtsOverlayEvents.Add(repeat);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { repeat.Id });
        });

        app.MapDelete("/api/admin/tts/{id:long}", async (long id, IDbContextFactory<MyStreamBotDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var item = await db.TtsOverlayEvents.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (item is null) return Results.NotFound();
            db.TtsOverlayEvents.Remove(item);
            await db.SaveChangesAsync(ct);
            return Results.Ok();
        });

        app.MapGet("/api/admin/users", async (string? q, int? page, int? pageSize, IDbContextFactory<MyStreamBotDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var query = db.Users.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(q)) query = query.Where(x => x.Username.Contains(q.Trim()));
            var size = Math.Clamp(pageSize ?? 20, 5, 100);
            var total = await query.LongCountAsync(ct);
            var pages = Math.Max(1L, (total + size - 1) / size);
            var currentPage = (int)Math.Clamp(page ?? 1, 1, pages);
            var items = await query
                .OrderByDescending(x => x.Points)
                .ThenBy(x => x.Username)
                .Skip((currentPage - 1) * size)
                .Take(size)
                .Select(x => new { x.Id, x.Username, x.AvatarUrl, Points = x.Points, Platform = x.Platform.ToString(), x.PlatformUserId })
                .ToListAsync(ct);
            return Results.Ok(new { items, page = currentPage, pageSize = size, total, pages });
        });

        app.MapPost("/api/admin/users/{id:long}/points", async (long id, PointsRequest request, IDbContextFactory<MyStreamBotDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var user = await db.Users.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (user is null) return Results.NotFound();
            checked { user.Points += request.Amount; }
            db.PointTransactions.Add(new MyStreamBot.Core.Entities.PointTransaction
            {
                UserId = user.Id,
                Amount = request.Amount,
                Type = request.Amount >= 0 ? PointTransactionType.Admin : PointTransactionType.Penalty,
                Description = request.Reason ?? "Alteração administrativa",
                CreatedAtUtc = DateTime.UtcNow,
                SourceMessageKey = $"admin:{Guid.NewGuid():N}"
            });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Ok(new { user.Id, user.Username, user.Points });
        });
    }

    private static async Task<MyStreamBot.Core.Entities.TtsAdminSettings> GetSettingsAsync(MyStreamBotDbContext db, CancellationToken ct)
    {
        var settings = await db.TtsAdminSettings.FirstOrDefaultAsync(x => x.Id == 1, ct);
        if (settings is not null) return settings;
        settings = new() { Id = 1 };
        db.TtsAdminSettings.Add(settings);
        await db.SaveChangesAsync(ct);
        return settings;
    }

    private sealed record AdminSettingsRequest(bool AcceptCommands, bool PublishEnabled);
    private sealed record BlockViewerRequest(string Platform, string PlatformUserId, string Username);
    private sealed record PointsRequest(long Amount, string? Reason);

    private static string AdminHtml() => """
<!doctype html>
<html lang="pt-BR">
<head>
<meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>MyStreamBot Admin</title>
<style>
*{box-sizing:border-box}body{margin:0;background:#090b10;color:#eee;font-family:Arial,sans-serif}header{padding:22px 28px;border-bottom:1px solid #252b36;position:sticky;top:0;background:#090b10ee;backdrop-filter:blur(8px);z-index:2}h1{margin:0;font-size:24px}.muted{color:#8f99aa;font-size:12px}main{max-width:1200px;margin:auto;padding:22px}.card{background:#121620;border:1px solid #252c39;border-radius:15px;padding:18px}.grid{display:grid;grid-template-columns:1fr 1fr;gap:16px}.tabs{display:flex;gap:8px;margin-bottom:16px;padding:5px;background:#10141c;border:1px solid #252c39;border-radius:12px}.tab{flex:1;border:0;border-radius:8px;padding:12px;background:transparent;color:#9da7b7;font-weight:900;cursor:pointer}.tab.active{background:#28303d;color:#fff}.panel{display:none}.panel.active{display:block}.card h2{font-size:16px;margin:0 0 14px}.controls{display:flex;gap:9px;flex-wrap:wrap}.btn{border:0;border-radius:9px;padding:9px 13px;background:#28303d;color:white;font-weight:800;cursor:pointer}.green{background:#176b42}.red{background:#7b222a}.orange{background:#825b13}.status{font-size:18px;font-weight:900;margin-bottom:12px}.row{display:flex;align-items:center;gap:10px;padding:10px 0;border-bottom:1px solid #252c39}.grow{flex:1;min-width:0}.name{font-weight:800}.small{font-size:11px;color:#9da7b7;margin-top:3px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.search{width:100%;padding:10px;border-radius:8px;border:1px solid #303846;background:#0c1017;color:#fff;margin-bottom:10px}.tools{display:flex;gap:6px}.tools input{width:90px;padding:8px;border-radius:7px;border:1px solid #303846;background:#0c1017;color:#fff}.table{width:100%;border-collapse:collapse}.table th,.table td{padding:9px;border-bottom:1px solid #252c39;text-align:left;font-size:12px}.table th{color:#9da7b7;text-transform:uppercase;font-size:10px}.pager{display:flex;align-items:center;justify-content:center;gap:6px;margin-top:14px;flex-wrap:wrap}.pagebtn{min-width:34px;border:1px solid #303846;border-radius:8px;padding:8px 10px;background:#0c1017;color:#fff;cursor:pointer}.pagebtn.active{background:#176b42;border-color:#176b42;font-weight:900}.pagebtn:disabled{opacity:.4;cursor:not-allowed}.pager-info{text-align:center;color:#8f99aa;font-size:11px;margin-top:8px}.toast{position:fixed;right:20px;bottom:20px;background:#202a38;padding:12px 16px;border-radius:9px;display:none;z-index:5}.empty{text-align:center;padding:25px;color:#8f99aa}@media(max-width:800px){.grid{grid-template-columns:1fr}.tabs{flex-direction:column}}
</style></head>
<body>
<header><h1>🎛️ MyStreamBot — TTS Admin</h1><div class="muted">Controle de voz, viewers, pontos e fila do overlay</div></header>
<main>
<div class="tabs">
<button class="tab active" data-tab="control">🎛️ Controle</button>
<button class="tab" data-tab="viewers">👥 Viewers / Pontos</button>
<button class="tab" data-tab="history">🔊 Histórico de voz</button>
</div>
<section id="control" class="panel active">
<div class="grid">
<section class="card"><h2>🔊 Controle geral</h2><div id="status" class="status">Carregando...</div><div class="controls"><button id="commands" class="btn" onclick="toggleCommands()"></button><button id="publish" class="btn" onclick="togglePublish()"></button></div><p class="muted">Bloquear !voz rejeita novos comandos. Pausar o overlay impede a reprodução de novos itens.</p></section>
<section class="card"><h2>⚡ Ações rápidas</h2><div class="controls"><button class="btn orange" onclick="repeatLast()">▶ Repetir último</button><button class="btn red" onclick="clearQueue()">🗑 Limpar fila</button></div></section>
</div>
<section class="card" style="margin-top:16px"><h2>🚫 Viewers bloqueados</h2><div id="blocked">Carregando...</div></section>
</section>
<section id="viewers" class="panel">
<section class="card"><h2>👥 Pontos / viewers</h2><input id="search" class="search" placeholder="Pesquisar viewer..." oninput="searchUsers()"><div id="users"></div><div id="usersPager"></div></section>
</section>
<section id="history" class="panel">
<section class="card"><h2>🔊 Histórico de !voz</h2><div class="muted" style="margin-bottom:10px">▶ repete o TTS • 🗑 remove o registro</div><div style="overflow:auto"><table class="table"><thead><tr><th>Hora</th><th>Viewer</th><th>Texto</th><th>Ações</th></tr></thead><tbody id="historyRows"></tbody></table></div><div id="historyPager"></div></section>
</section>
</main><div id="toast" class="toast"></div>
<script>
let state={acceptCommands:true,publishEnabled:true};
let usersPage=1,historyPage=1,searchTimer=null;
const pageSize=20,$=x=>document.getElementById(x);
const esc=x=>String(x??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const fmt=x=>Number(x||0).toLocaleString('pt-BR');
function toast(x){$('toast').textContent=x;$('toast').style.display='block';setTimeout(()=>$('toast').style.display='none',2000)}
async function api(url,opt={}){const r=await fetch(url,{headers:{'Content-Type':'application/json'},...opt});if(!r.ok)throw Error('HTTP '+r.status);return r.json()}
function render(){ $('status').textContent=state.acceptCommands?'🟢 !voz ATIVO':'🔴 !voz BLOQUEADO'; $('commands').textContent=state.acceptCommands?'🔴 Bloquear !voz':'🟢 Liberar !voz'; $('commands').className='btn '+(state.acceptCommands?'red':'green'); $('publish').textContent=state.publishEnabled?'⏸ Pausar overlay':'▶ Retomar overlay'; $('publish').className='btn '+(state.publishEnabled?'orange':'green') }
function tabs(){document.querySelectorAll('.tab').forEach(b=>b.onclick=()=>{document.querySelectorAll('.tab').forEach(x=>x.classList.remove('active'));document.querySelectorAll('.panel').forEach(x=>x.classList.remove('active'));b.classList.add('active');$(b.dataset.tab).classList.add('active');if(b.dataset.tab==='viewers')loadUsers();if(b.dataset.tab==='history')loadHistory()})}
async function load(){try{const x=await api('/api/admin/tts/status');state=x;render();$('blocked').innerHTML=x.blocked.length?x.blocked.map(b=>`<div class="row"><div class="grow"><div class="name">${esc(b.username)}</div><div class="small">${esc(b.platform)} • ${esc(b.platformUserId)}</div></div><button class="btn" onclick="unblock('${esc(b.platform)}','${esc(b.platformUserId)}')">Desbloquear</button></div>`).join(''):'<div class="empty">Nenhum viewer bloqueado.</div>'}catch(e){toast(e.message)}}
async function save(){state=await api('/api/admin/tts/settings',{method:'POST',body:JSON.stringify(state)});render();toast('Configuração salva')}
async function toggleCommands(){state.acceptCommands=!state.acceptCommands;await save()}
async function togglePublish(){state.publishEnabled=!state.publishEnabled;await save()}
async function unblock(p,id){await api('/api/admin/tts/block?platform='+encodeURIComponent(p)+'&platformUserId='+encodeURIComponent(id),{method:'DELETE'});load()}
async function block(p,id,name){await api('/api/admin/tts/block',{method:'POST',body:JSON.stringify({platform:p,platformUserId:id,username:name})});toast(name+' bloqueado');loadUsers()}
function searchUsers(){clearTimeout(searchTimer);usersPage=1;searchTimer=setTimeout(loadUsers,250)}
function pager(target,page,pages,total,onPage){if(!total){$(target).innerHTML='';return}let html='<div class="pager"><button class="pagebtn" '+(page<=1?'disabled':'')+' onclick="'+onPage+'('+(page-1)+')">‹</button>';const start=Math.max(1,page-2),end=Math.min(pages,page+2);if(start>1)html+='<button class="pagebtn" onclick="'+onPage+'(1)">1</button>'+(start>2?'<span class="muted">…</span>':'');for(let i=start;i<=end;i++)html+='<button class="pagebtn '+(i===page?'active':'')+'" onclick="'+onPage+'('+i+')">'+i+'</button>';if(end<pages)html+=(end<pages-1?'<span class="muted">…</span>':'')+'<button class="pagebtn" onclick="'+onPage+'('+pages+')">'+pages+'</button>';html+='<button class="pagebtn" '+(page>=pages?'disabled':'')+' onclick="'+onPage+'('+(page+1)+')">›</button></div><div class="pager-info">Página '+page+' de '+pages+' • '+fmt(total)+' registros</div>';$(target).innerHTML=html}
async function loadUsers(){try{const q=encodeURIComponent($('search').value.trim());const x=await api('/api/admin/users?q='+q+'&page='+usersPage+'&pageSize='+pageSize);usersPage=x.page;$('users').innerHTML=x.items.length?x.items.map(u=>`<div class="row"><div class="grow"><div class="name">${esc(u.username)}</div><div class="small">${esc(u.platform)} • saldo ${fmt(u.points)}</div></div><button class="btn red" onclick="block('${esc(u.platform)}','${esc(u.platformUserId)}','${esc(u.username)}')">🚫</button><div class="tools"><input id="p${u.id}" type="number" placeholder="±"><button class="btn" onclick="points(${u.id})">🪙</button></div></div>`).join(''):'<div class="empty">Nenhum viewer encontrado.</div>';pager('usersPager',x.page,x.pages,x.total,'goUsers')}catch(e){toast(e.message)}}
function goUsers(p){usersPage=p;loadUsers()}
async function points(id){const amount=Number($('p'+id).value);if(!amount)return;await api('/api/admin/users/'+id+'/points',{method:'POST',body:JSON.stringify({amount,reason:'Alteração administrativa'})});toast('Pontos alterados');loadUsers()}
async function loadHistory(){try{const x=await api('/api/admin/tts/history?page='+historyPage+'&pageSize='+pageSize);historyPage=x.page;$('historyRows').innerHTML=x.items.length?x.items.map(v=>`<tr><td>${new Date(v.createdAtUtc).toLocaleString('pt-BR')}</td><td>${esc(v.username)}${v.isRepeat?' <small>(repeat)</small>':''}</td><td>${esc(v.text)}</td><td><button class="btn" onclick="repeat(${v.id})">▶</button> <button class="btn red" onclick="removeEvent(${v.id})">🗑</button></td></tr>`).join(''):'<tr><td colspan="4" class="empty">Nenhum TTS.</td></tr>';pager('historyPager',x.page,x.pages,x.total,'goHistory')}catch(e){toast(e.message)}}
function goHistory(p){historyPage=p;loadHistory()}
async function repeat(id){await api('/api/admin/tts/'+id+'/repeat',{method:'POST'});toast('Repetido');loadHistory()}
async function removeEvent(id){await api('/api/admin/tts/'+id,{method:'DELETE'});toast('Removido');loadHistory()}
async function repeatLast(){await api('/api/tts/repeat-last',{method:'POST'});toast('Último TTS repetido')}
async function clearQueue(){if(!confirm('Remover os eventos atuais do histórico/fila?'))return;while(true){const x=await api('/api/admin/tts/history?page=1&pageSize=100');if(!x.items.length)break;for(const v of x.items)await fetch('/api/admin/tts/'+v.id,{method:'DELETE'});if(x.items.length<100)break}toast('Fila limpa');loadHistory()}
tabs();load();
</script></body></html>
""";
}
