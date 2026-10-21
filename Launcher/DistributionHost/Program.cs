using System.Text.RegularExpressions;

if (args.Length != 2 || !Uri.TryCreate(args[1], UriKind.Absolute, out var url) || !url.IsLoopback || url.Scheme != "http")
    throw new ArgumentException("Usage: DistributionHost DISTRIBUTION_DIRECTORY http://127.0.0.1:5095 (loopback only)");
var root = Path.GetFullPath(args[0]);
var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls(args[1]);
var app = builder.Build();
app.MapGet("/blobs/{hash}", (string hash) =>
{
    if (!Regex.IsMatch(hash, "^[a-f0-9]{64}$")) return Results.BadRequest();
    var path = Path.Combine(root, "blobs", hash);
    return File.Exists(path) ? Results.File(path, "application/octet-stream", enableRangeProcessing: true) : Results.NotFound();
});
app.MapGet("/{channel}/{name}", (string channel, string name) =>
{
    if (channel is not ("development" or "testing" or "live") || !Regex.IsMatch(name, @"^(game|launcher)(-\d+)?\.json$")) return Results.NotFound();
    var path = Path.Combine(root, channel, name);
    return File.Exists(path) ? Results.File(path, "application/json") : Results.NotFound();
});
await app.RunAsync();
