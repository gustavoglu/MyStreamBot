var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.MapGet("/", () => Results.Content("<!doctype html><html lang='pt-BR'><head><meta charset='utf-8'><title>MyStreamBot Overlay</title><style>body{margin:0;background:transparent;color:white;font-family:Arial,sans-serif}.card{width:360px;padding:18px;border-radius:16px;background:rgba(15,15,20,.86);box-shadow:0 8px 30px #0008}h2{margin:0 0 12px}.row{display:flex;justify-content:space-between;padding:5px 0}</style></head><body><div class='card'><h2>🪙 MyStreamBot</h2><div class='row'><span>🥇 João</span><b>45.200</b></div><div class='row'><span>🥈 Maria</span><b>38.100</b></div><div class='row'><span>🥉 Carlos</span><b>31.750</b></div></div></body></html>", "text/html"));
app.Run();
