using System.Text.Json.Nodes;

namespace Relay.Core;

// Application services depend only on this boundary, not on process management or WPF.
public interface ITransferEngine
{
    string DeviceId { get; }
    Task<JsonObject> GetAsync(string resource, CancellationToken ct = default);
    Task<JsonNode?> ReadAsync(string resource, CancellationToken ct = default);
    Task WriteAsync(string resource, JsonNode? body, string method = "POST", CancellationToken ct = default);
    Task WithStoppedAsync(Func<Task> operation);
}

public static class DeviceIdentity
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    public static string Normalize(string input)
    {
        var raw = input.Replace("-", "").Replace(" ", "").Replace("\r", "").Replace("\n", "").Trim().ToUpperInvariant();
        if (raw.Length != 56 || raw.Any(c => !Alphabet.Contains(c)))
            throw new ArgumentException("請貼上完整的 8 組裝置 ID（不是短代碼）。");
        // Syncthing uses four Luhn-mod-32 check digits, each following 13 data characters.
        for (var block = 0; block < 4; block++)
        {
            var sum = 0;
            var factor = 1;
            for (var i = 12; i >= 0; i--)
            {
                var n = Alphabet.IndexOf(raw[block * 14 + i]) * factor;
                sum += n / 32 + n % 32;
                factor = factor == 1 ? 2 : 1;
            }
            if (raw[block * 14 + 13] != Alphabet[(32 - sum % 32) % 32])
                throw new ArgumentException("裝置 ID 檢查碼不正確，請重新複製。");
        }
        return string.Join('-', Enumerable.Range(0, 8).Select(i => raw.Substring(i * 7, 7)));
    }
}

public sealed record Friend(string DeviceId, string Name, bool Connected, string Connection)
{
    public string Display => $"{Name}  ·  {(Connected ? (Connection.Contains("relay") ? "已連線 · 中繼" : "已連線 · 直接") : "離線／等待對方確認")}";
}

public sealed class PairingService(ITransferEngine engine)
{
    public async Task AddAsync(string id, string name, bool identityVerified, string[]? addresses = null)
    {
        if (!identityVerified) throw new InvalidOperationException("請先透過可信管道核對雙方完整裝置 ID。");
        id = DeviceIdentity.Normalize(id);
        if (id == engine.DeviceId) throw new ArgumentException("這是你自己的裝置。");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80) throw new ArgumentException("請填寫 1～80 字的朋友名稱。");
        var template = await engine.GetAsync("config/defaults/device");
        template["deviceID"] = id;
        template["name"] = name.Trim();
        template["addresses"] = new JsonArray((addresses ?? ["dynamic"]).Select(a => JsonValue.Create(a)).ToArray());
        template["introducer"] = false;
        template["autoAcceptFolders"] = false;
        await engine.WriteAsync("config/devices", template);
    }

    public async Task<IReadOnlyList<Friend>> ListAsync()
    {
        var devices = (JsonArray)(await engine.ReadAsync("config/devices"))!;
        var connections = (await engine.GetAsync("system/connections"))["connections"]!.AsObject();
        return devices.Where(d => d!["deviceID"]!.GetValue<string>() != engine.DeviceId).Select(d =>
        {
            var id = d!["deviceID"]!.GetValue<string>();
            var c = connections[id];
            return new Friend(id, d["name"]?.GetValue<string>() ?? id, c?["connected"]?.GetValue<bool>() == true, c?["type"]?.GetValue<string>() ?? "");
        }).ToArray();
    }

    public async Task RemoveAsync(string id)
    {
        id = DeviceIdentity.Normalize(id);
        if (id == engine.DeviceId) throw new InvalidOperationException("不能移除本機裝置。");
        // Revoke every folder permission before removing the device. Never delete user files.
        var folders = (JsonArray)(await engine.ReadAsync("config/folders"))!;
        foreach (var f in folders)
        {
            var devices = f!["devices"]!.AsArray();
            var keep = devices.Where(d => d!["deviceID"]!.GetValue<string>() != id).Select(d => d!.DeepClone()).ToArray();
            if (keep.Length != devices.Count)
            {
                f["devices"] = new JsonArray(keep);
                await engine.WriteAsync("config/folders/" + Uri.EscapeDataString(f["id"]!.GetValue<string>()), f, "PUT");
            }
        }
        await engine.WriteAsync("config/devices/" + id, null, "DELETE");
    }
}
