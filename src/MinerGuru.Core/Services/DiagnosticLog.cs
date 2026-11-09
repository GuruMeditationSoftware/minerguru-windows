using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MinerGuru.Core.Models;

namespace MinerGuru.Core.Services;

/// <summary>
/// A small local log for finding problems after days of real use: states, alerts, writes to miners and
/// regular measurements. It leaves the device only when the user exports it, so it holds no passwords,
/// wallet addresses, pool users, one-time codes or names, only the miners' addresses on the local network.
/// </summary>
public sealed class DiagnosticLog(string? path = null)
{
    public static DiagnosticLog Shared { get; } = new();

    /// <summary>Two files of this size at most: the current one and the one before it.</summary>
    public const long MaxBytes = 1_000_000;

    private readonly object _lock = new();
    private string? _path = path;

    /// <summary>The running app turns the log on; tests write nothing.</summary>
    public void Activate(string dataDirectory)
    {
        lock (_lock) _path = Path.Combine(dataDirectory, "minerguru.log");
    }

    private static string Older(string path) => Path.Combine(Path.GetDirectoryName(path)!, "minerguru.1.log");

    public void Write(string category, string message, DateTime? at = null)
    {
        lock (_lock)
        {
            if (_path is not { } path) return;
            var stamp = new DateTimeOffset(at ?? DateTime.Now).ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
            var line = $"{stamp} [{category}] {message.ReplaceLineEndings(" ")}\n";
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length + line.Length > MaxBytes) File.Move(path, Older(path), overwrite: true);
                File.AppendAllText(path, line);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A log must never get in the way of the app.
            }
        }
    }

    /// <summary>Everything still on disk, oldest first.</summary>
    public string ExportText()
    {
        lock (_lock)
        {
            if (_path is not { } path) return "";
            return string.Concat(new[] { Older(path), path }.Where(File.Exists).Select(File.ReadAllText));
        }
    }

    /// <summary>Model, firmware and the names (never the values) of the fields a miner reports.</summary>
    public static string Profile(MinerDevice miner, MinerSystemInfo info) =>
        $"model={info.DetectedHardwareTitle()} firmware={info.FirmwareVersion} axeos={info.AxeOsVersion ?? "-"} keys={FieldNames(miner.RawJson)}";

    /// <summary>The top-level names of a JSON object, sorted.</summary>
    public static string FieldNames(string? json)
    {
        try
        {
            return JsonNode.Parse(json ?? "{}") is JsonObject o ? string.Join(",", o.Select(p => p.Key).Order(StringComparer.Ordinal)) : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }
}
