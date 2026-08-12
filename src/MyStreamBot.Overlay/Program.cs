using Microsoft.EntityFrameworkCore;
using MyStreamBot.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

var dbPath = ResolveDatabasePath();

builder.Services.AddDbContextFactory<MyStreamBotDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"));

var app = builder.Build();

app.MapGet("/", () => Results.Content(Html(), "text/html; charset=utf-8"));

app.MapGet("/api/top", async (IDbContextFactory<MyStreamBotDbContext> factory, CancellationToken ct) =>
{
    await using var db = await factory.CreateDbContextAsync(ct);

    var users = await db.Users
        .AsNoTracking()
        .Where(x => x.Points > 0)
        .OrderByDescending(x => x.Points)
        .ThenBy(x => x.Username)
        .Take(10)
        .Select(x => new
        {
            x.Id,
            x.Username,
            x.AvatarUrl,
            x.Points
        })
        .ToListAsync(ct);

    return Results.Ok(users);
});

app.MapGet("/api/recent", async (IDbContextFactory<MyStreamBotDbContext> factory, CancellationToken ct) =>
{
    await using var db = await factory.CreateDbContextAsync(ct);

    var updates = await db.PointTransactions
        .AsNoTracking()
        .Where(x => x.Amount > 0)
        .OrderByDescending(x => x.CreatedAtUtc)
        .Take(5)
        .Select(x => new
        {
            x.Id,
            x.Amount,
            x.Type,
            x.Description,
            x.CreatedAtUtc,
            User = x.User == null ? null : new
            {
                x.User.Username,
                x.User.AvatarUrl,
                x.User.Points
            }
        })
        .ToListAsync(ct);

    return Results.Ok(updates);
});

app.Run();

static string ResolveDatabasePath()
{
    var directory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MyStreamBot");

    Directory.CreateDirectory(directory);
    return Path.Combine(directory, "mystreambot.db");
}

static string Html() => """
<!doctype html>
<html lang="pt-BR">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>MyStreamBot Overlay</title>
<style>
:root { color-scheme: dark; }
* { box-sizing: border-box; }
html, body { margin:0; padding:0; background:transparent; font-family:Arial,Helvetica,sans-serif; color:#fff; }
body { width:460px; }
.panel { padding:14px; border-radius:18px; background:rgba(12,14,20,.90); box-shadow:0 10px 35px rgba(0,0,0,.35); backdrop-filter:blur(8px); }
.title { font-size:19px; font-weight:800; margin:0 0 10px; }
.subtitle { color:#aeb5c2; font-size:12px; margin-top:-6px; margin-bottom:12px; }
.update { display:flex; align-items:center; gap:11px; min-height:66px; padding:9px 10px; margin:7px 0; border-radius:13px; background:rgba(255,255,255,.065); overflow:hidden; }
.update.new { animation:enter .42s ease-out; }
.avatar { width:44px; height:44px; border-radius:50%; object-fit:cover; background:#303540; flex:0 0 44px; }
.info { min-width:0; flex:1; }
.name { font-size:14px; font-weight:750; white-space:nowrap; overflow:hidden; text-overflow:ellipsis; }
.type { color:#8993a3; font-size:10px; text-transform:uppercase; letter-spacing:.7px; margin-top:4px; }
.reward { display:flex; align-items:baseline; justify-content:flex-end; gap:8px; flex:0 0 auto; white-space:nowrap; }
.total-now { color:#d5dbe5; font-size:19px; font-weight:900; }
.gain { font-size:19px; font-weight:900; }
.gain.positive { color:#6dff9b; }
.empty { color:#8f98a7; font-size:13px; padding:16px 4px; }
@keyframes enter { from { opacity:0; transform:translateY(-18px) scale(.98); } to { opacity:1; transform:translateY(0) scale(1); } }
</style>
</head>
<body>
<div class="panel">
  <div class="title">⚡ Últimas atualizações</div>
  <div class="subtitle">Pontos recebidos pelos viewers</div>
  <div id="updates"><div class="empty">Aguardando atualizações...</div></div>
</div>
<script>
const fallbackAvatar = 'data:image/svg+xml;charset=UTF-8,' + encodeURIComponent('<svg xmlns="http://www.w3.org/2000/svg" width="80" height="80"><rect width="100%" height="100%" fill="#303540"/><circle cx="40" cy="31" r="14" fill="#9aa3b2"/><path d="M15 72c3-17 47-17 50 0" fill="#9aa3b2"/></svg>');
let knownIds = new Set();

function formatPoints(value) { return Number(value || 0).toLocaleString('pt-BR'); }
function typeLabel(type) {
  const labels = { MessageReward:'MENSAGEM', Follow:'FOLLOW', Subscription:'INSCRIÇÃO', Gift:'GIFT', Donation:'DOAÇÃO', Bonus:'BÔNUS', Admin:'ADMIN', Penalty:'PENALIDADE', BetWin:'APOSTA' };
  return labels[type] || String(type || 'PONTOS').toUpperCase();
}

async function refresh() {
  try {
    const response = await fetch('/api/recent?ts=' + Date.now(), { cache:'no-store' });
    const items = await response.json();
    const root = document.getElementById('updates');
    root.innerHTML = '';
    if (!items.length) { root.innerHTML = '<div class="empty">Aguardando atualizações...</div>'; return; }
    for (const item of items) {
      const user = item.user;
      if (!user) continue;
      const row = document.createElement('div');
      row.className = 'update' + (knownIds.has(item.id) ? '' : ' new');
      row.innerHTML = `
        <img class="avatar" src="${user.avatarUrl || fallbackAvatar}" onerror="this.src='${fallbackAvatar}'">
        <div class="info">
          <div class="name">${escapeHtml(user.username)}</div>
          <div class="type">${typeLabel(item.type)}</div>
        </div>
        <div class="reward">
          <div class="total-now">(${formatPoints(user.points)})</div>
          <div class="gain positive">+${formatPoints(item.amount)}</div>
        </div>`;
      root.appendChild(row);
      knownIds.add(item.id);
    }
    while (knownIds.size > 50) knownIds.delete(knownIds.values().next().value);
  } catch { /* Worker/banco ainda pode estar iniciando. */ }
}
function escapeHtml(value) {
  return String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
}
refresh();
setInterval(refresh, 1000);
</script>
</body>
</html>
""";
