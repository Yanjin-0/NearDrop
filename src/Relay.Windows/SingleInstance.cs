using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Relay.Windows;

public sealed class SingleInstance : IDisposable
{
    private readonly string pipeName;
    private FileStream? ownership;
    private readonly CancellationTokenSource stop = new();
    private Task? listener;
    public SingleInstance(string profile)
    {
        PrivateStorage.Create(profile);
        pipeName = "NearDrop-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(profile).ToUpperInvariant())))[..24];
    }
    public bool TryOwn(string profile)
    {
        try { ownership = new FileStream(Path.Combine(profile, "desktop.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); return true; }
        catch (IOException) { return false; }
    }
    public async Task ForwardAsync(string[] paths)
    {
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var timeout = new CancellationTokenSource(3000);
        await pipe.ConnectAsync(timeout.Token);
        await JsonSerializer.SerializeAsync(pipe, paths, cancellationToken: timeout.Token); await pipe.FlushAsync(timeout.Token);
    }
    public void Listen(Action<string[]> onPaths)
    {
        listener = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(stop.Token);
                    using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); readTimeout.CancelAfter(3000);
                    using var data = new MemoryStream(); var buffer = new byte[8192]; int count;
                    while ((count = await pipe.ReadAsync(buffer, readTimeout.Token)) > 0)
                    {
                        if (data.Length + count > 1024 * 1024) throw new IOException("Too many selected paths");
                        data.Write(buffer, 0, count);
                    }
                    var paths = JsonSerializer.Deserialize<string[]>(data.ToArray());
                    if (paths != null && paths.Length <= 4096) onPaths(paths);
                }
                catch (Exception e) when (e is IOException or JsonException or OperationCanceledException) { }
            }
        });
    }
    public void Dispose()
    {
        stop.Cancel();
        listener?.GetAwaiter().GetResult();
        ownership?.Dispose();
        stop.Dispose();
    }
}
