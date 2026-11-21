using System.Text.Json.Nodes;

namespace Relay.Core;

public static class ReceiveDiskPolicy
{
    public const int ReserveGB = 2;
    private const long ReserveBytes = ReserveGB * 1_000_000_000L;
    public static JsonObject EngineSetting() => new() { ["value"] = ReserveGB, ["unit"] = "GB" };
    public static bool HasRoom(long freeBytes, long payloadBytes = 0) =>
        freeBytes >= ReserveBytes && payloadBytes >= 0 && payloadBytes <= freeBytes - ReserveBytes;
    public static void RequireRoom(string path, long payloadBytes = 0)
    {
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!);
        if (!HasRoom(drive.AvailableFreeSpace, payloadBytes))
            throw new IOException($"{drive.Name} 空間不足。請清出足夠空間並保留至少 {ReserveGB} GB，再重試。");
    }
}
