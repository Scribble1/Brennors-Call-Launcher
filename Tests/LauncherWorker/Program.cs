using System.IO.Pipes;
using System.Text.Json;

var pipeName = Environment.GetEnvironmentVariable("BC_LAUNCH_PIPE") ?? throw new InvalidOperationException("Test worker only.");
var runtime = args.Single(a => a.StartsWith("-UserDir="))[9..];
using var versionLock = File.Open(Environment.GetEnvironmentVariable("BC_VERSION_LOCK")!, FileMode.Open, FileAccess.Read, FileShare.Read);
using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
await Task.Delay(750); // Give the parent security test time to attempt a wrong-PID connection.
await pipe.ConnectAsync(10000);
var length = new byte[4]; await pipe.ReadExactlyAsync(length);
var bytes = new byte[BitConverter.ToInt32(length)]; await pipe.ReadExactlyAsync(bytes);
using var document = JsonDocument.Parse(bytes); var message = document.RootElement;
File.WriteAllText(Path.Combine(runtime, "identity.json"), JsonSerializer.Serialize(new
{
    account = message.GetProperty("accountName").GetString(), character = message.GetProperty("characterId").GetString(),
    hasInheritedSecret = Environment.GetEnvironmentVariable("WORLD_API_KEY") is not null,
    temp = Environment.GetEnvironmentVariable("TEMP")
}));
await pipe.WriteAsync(new byte[] { 1 }); await pipe.FlushAsync();
await Task.Delay(TimeSpan.FromMinutes(2));
