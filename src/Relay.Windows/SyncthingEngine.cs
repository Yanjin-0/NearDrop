using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Relay.Core;

namespace Relay.Windows;

public sealed class SyncthingEngine : ITransferEngine, IAsyncDisposable
{
    public const string Version = "2.1.7";
    public const string ExecutableHash = "CD9AEA20AFDA2B9BDBA5AFF2E6AC964ED5C2E741ED4CEB48AD9E1BE096CA7891";
    private Process? process;
    private ProcessJob? job;
    private FileStream? ownership;
    private HttpClient? client;
    private readonly string executable, home;
    private readonly bool localTest;
    public string DeviceId { get; private set; } = "";
    public int GuiPort { get; private set; }
    public int ListenPort { get; private set; }
    public bool Running => process is { HasExited: false };

    public SyncthingEngine(string executable, string home, bool localTest = false)
    {
        this.executable = Path.GetFullPath(executable);
        this.home = Path.GetFullPath(home);
        this.localTest = localTest;
    }

    public Task StartAsync(CancellationToken ct = default) => StartInternalAsync(false, ct);
    private async Task StartInternalAsync(bool resume, CancellationToken ct = default)
    {
        if (!resume && ownership != null) throw new InvalidOperationException("引擎已啟動。");
        try
        {
            PrivateStorage.Create(home);
            if (!resume) ownership = new FileStream(Path.Combine(home, "app.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            await using (var stream = File.OpenRead(executable))
            {
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
                if (!actual.Equals(ExecutableHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("傳輸元件完整性驗證失敗，請重新安裝官方封裝。");
            }
            job = new ProcessJob();
            var configFile = Path.Combine(home, "config.xml");
            var first = !File.Exists(configFile);
            if (first && (File.Exists(Path.Combine(home, "cert.pem")) || File.Exists(Path.Combine(home, "key.pem"))))
                throw new IOException("設定遺失但裝置金鑰仍存在；已停止，請先備份並恢復設定。");
            if (first)
            {
                using var generate = StartProcess(["generate", "--home=" + home, "--no-port-probing", "--gui-user=local", "--gui-password=-"]);
                await generate.StandardInput.WriteLineAsync(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
                generate.StandardInput.Close();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                try { await generate.WaitForExitAsync(timeout.Token); }
                catch { if (!generate.HasExited) generate.Kill(true); throw; }
                if (generate.ExitCode != 0) throw new IOException("無法建立裝置設定。");
            }
            if (!File.Exists(Path.Combine(home, "cert.pem")) || !File.Exists(Path.Combine(home, "key.pem")))
                throw new IOException("裝置金鑰遺失；不會自動更換你的裝置身分。");
            var config = XDocument.Load(configFile);
            var root = config.Root!;
            foreach (var folder in root.Elements("folder"))
            {
                var id = (string?)folder.Attribute("id");
                if (id == null || !(id.StartsWith("nd-once-", StringComparison.Ordinal) || id.StartsWith("nd-live-", StringComparison.Ordinal))) continue;
                folder.SetElementValue("junctionsAsDirs", false);
                if ((string?)folder.Attribute("type") == "receiveonly")
                {
                    folder.SetElementValue("minDiskFree", ReceiveDiskPolicy.ReserveGB);
                    folder.Element("minDiskFree")!.SetAttributeValue("unit", "GB");
                }
            }
            if (first) root.Elements("folder").Remove(); // Never expose Syncthing's default Sync folder.
            var gui = root.Element("gui")!;
            GuiPort = FreePort();
            gui.SetAttributeValue("enabled", "true");
            gui.SetAttributeValue("tls", "false");
            gui.SetElementValue("address", "127.0.0.1:" + GuiPort);
            gui.SetElementValue("insecureAdminAccess", "false");
            gui.SetElementValue("insecureSkipHostcheck", "false");
            gui.SetElementValue("insecureAllowFrameLoading", "false");
            var apiKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            // Engine's native config necessarily stores an API key; directory ACL isolates it.
            gui.SetElementValue("apikey", apiKey);
            foreach (var d in root.Elements("device"))
            {
                d.SetAttributeValue("introducer", "false");
                d.SetElementValue("autoAcceptFolders", "false");
            }
            var options = root.Element("options")!;
            options.SetElementValue("startBrowser", "false");
            options.SetElementValue("autoUpgradeIntervalH", "0");
            options.SetElementValue("upgradeToPreReleases", "false");
            options.SetElementValue("urAccepted", "-1");
            options.SetElementValue("crashReportingEnabled", "false");
            if (first || (localTest && !resume))
            {
                ListenPort = FreePort();
                options.Elements("listenAddress").Remove();
                options.Add(new XElement("listenAddress", $"tcp://{(localTest ? "127.0.0.1" : "0.0.0.0")}:{ListenPort}"));
                if (!localTest)
                {
                    options.Add(new XElement("listenAddress", $"quic://0.0.0.0:{ListenPort}"));
                    options.Add(new XElement("listenAddress", "dynamic+https://relays.syncthing.net/endpoint"));
                }
            }
            if (localTest)
                foreach (var key in new[] { "globalAnnounceEnabled", "localAnnounceEnabled", "relaysEnabled", "natEnabled" }) options.SetElementValue(key, "false");
            config.Save(configFile + ".new");
            File.Move(configFile + ".new", configFile, true);
            process = StartProcess(["serve", "--home=" + home, "--no-browser", "--no-console", "--no-restart", "--no-upgrade"]);
            client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
            { BaseAddress = new Uri($"http://127.0.0.1:{GuiPort}/rest/"), Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.Add("X-API-Key", apiKey);
            for (var attempt = 0; attempt < 100; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                if (process.HasExited) throw new IOException($"背景傳輸引擎啟動失敗（代碼 {process.ExitCode}）。");
                try
                {
                    DeviceId = (await GetAsync("system/status", ct))["myID"]!.GetValue<string>();
                    DeviceIdentity.Normalize(DeviceId);
                    return;
                }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested) { }
                await Task.Delay(250, ct);
            }
            throw new TimeoutException("傳輸引擎沒有回應，請檢查防毒軟體或重新啟動。");
        }
        catch { await DisposeAsync(); throw; }
    }

    private Process StartProcess(string[] args)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = home };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        // Do not let inherited Syncthing environment variables override the isolated profile.
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("ST", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        start.Environment["STNOUPGRADE"] = "1";
        start.Environment["STNORESTART"] = "1";
        var child = Process.Start(start) ?? throw new IOException("無法啟動傳輸元件。");
        try { job!.Add(child); }
        catch { child.Kill(true); child.Dispose(); throw; }
        // Drain output; never persist keys or filenames in app logs.
        child.OutputDataReceived += (_, _) => { };
        child.ErrorDataReceived += (_, _) => { };
        child.BeginOutputReadLine(); child.BeginErrorReadLine();
        return child;
    }
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
    }
    private async Task<JsonNode?> RequestAsync(string resource, HttpMethod method, JsonNode? body, CancellationToken ct)
    {
        if (client == null) throw new InvalidOperationException("引擎尚未啟動。");
        if (resource.Contains("://") || resource.StartsWith('/') || resource.Contains("..")) throw new ArgumentException("無效 API 路徑。");
        using var request = new HttpRequestMessage(method, resource);
        if (body != null) request.Content = JsonContent.Create(body);
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"傳輸引擎回覆 {(int)response.StatusCode}，操作未完成。", null, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync(ct);
        return string.IsNullOrWhiteSpace(content) ? null : JsonNode.Parse(content);
    }
    public async Task<JsonObject> GetAsync(string resource, CancellationToken ct = default) => (await ReadAsync(resource, ct))!.AsObject();
    public Task<JsonNode?> ReadAsync(string resource, CancellationToken ct = default) => RequestAsync(resource, HttpMethod.Get, null, ct);
    public async Task WriteAsync(string resource, JsonNode? body, string method = "POST", CancellationToken ct = default) =>
        _ = await RequestAsync(resource, new HttpMethod(method), body, ct);
    public async Task WithStoppedAsync(Func<Task> operation)
    {
        await StopChildAsync();
        try { await operation(); }
        finally { await StartInternalAsync(true); }
    }
    private async Task StopChildAsync()
    {
        if (process is { HasExited: false })
        {
            try { using var timeout = new CancellationTokenSource(4000); await WriteAsync("system/shutdown", null, ct: timeout.Token); }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException or InvalidOperationException) { }
            try { using var timeout = new CancellationTokenSource(5000); await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(true);
                using var timeout = new CancellationTokenSource(5000);
                await process.WaitForExitAsync(timeout.Token);
            }
        }
        job?.Dispose(); job = null;
        process?.Dispose(); process = null;
        client?.Dispose(); client = null;
    }
    public async ValueTask DisposeAsync()
    {
        await StopChildAsync();
        ownership?.Dispose(); ownership = null;
    }
}
