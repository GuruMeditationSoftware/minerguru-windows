using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MinerGuru.Core.Models;
using static MinerGuru.Core.Lang;

namespace MinerGuru.Core.Services;

/// <summary>Instead of writing: the user gave no code, or a schedule has no session.</summary>
public sealed class OtpException(string message) : MinerApiException(message)
{
    public static OtpException Cancelled() => new(L("Kein Einmalcode eingegeben, nichts geändert.", "No one-time code entered, nothing changed."));
    public static OtpException CodeNeeded() => new(L("Dieser Miner braucht einen Einmalcode.", "This miner needs a one-time code."));
}

/// <summary>
/// Sessions for miners that protect their settings with a one-time code. The code itself is never kept, the
/// sessions rest encrypted for this Windows user in <c>otp.json</c>. Schedules only use a stored session:
/// five refused requests lock the miner for five minutes. Used on the UI thread, like <see cref="MinerStore"/>.
/// </summary>
public sealed class OtpSessions
{
    private sealed record Stored(string ProtectedToken, DateTime ExpiresUtc);

    private readonly string _path;
    private readonly Func<string, string, Task<OtpSession>> _create;
    private readonly Func<DateTime> _clock;
    private readonly Dictionary<Guid, OtpSession> _sessions = [];
    private readonly SemaphoreSlim _asking = new(1, 1);
    private readonly Func<byte[], byte[]> _seal;

    /// <param name="create">Exchanges host and code for a session.</param>
    /// <param name="seal">Encrypts a token for the disk, by default for the current Windows user.</param>
    public OtpSessions(string dataDirectory, Func<string, string, Task<OtpSession>> create, Func<DateTime>? clock = null, Func<byte[], byte[]>? seal = null)
    {
        _path = Path.Combine(dataDirectory, "otp.json");
        _create = create;
        _clock = clock ?? (() => DateTime.UtcNow);
        _seal = seal ?? SealForUser;
        Load();
    }

    /// <summary>Asks for the code with miner name and what was wrong with the last try; <c>null</c> when the user cancels.</summary>
    public Func<string, string?, Task<string?>>? Prompt { get; set; }

    public event Action? Changed;

    /// <summary>The stored session of a miner, if it can still be used.</summary>
    public string? Token(Guid minerId) =>
        _sessions.TryGetValue(minerId, out var session) && session.IsUsable(_clock()) ? session.Token : null;

    /// <summary>Whether a schedule may write to this miner without anyone at the screen.</summary>
    public bool CanWriteUnattended(MinerDevice miner) => !miner.RequiresOtp || Token(miner.Id) != null;

    public void Invalidate(Guid minerId)
    {
        if (_sessions.Remove(minerId)) Persist();
    }

    /// <summary>Returns a valid session, asking for a code if there is none; <c>null</c> when the miner no longer uses OTP.</summary>
    public async Task<string?> AuthorizeAsync(MinerDevice miner)
    {
        if (Token(miner.Id) is { } token) return token;
        await _asking.WaitAsync();
        try
        {
            return Token(miner.Id) ?? await AskForCodeAsync(miner);
        }
        finally
        {
            _asking.Release();
        }
    }

    private async Task<string?> AskForCodeAsync(MinerDevice miner)
    {
        if (Prompt == null) throw OtpException.Cancelled();
        string? problem = null;
        while (true)
        {
            var code = (await Prompt(miner.DisplayName, problem))?.Trim() ?? throw OtpException.Cancelled();
            if (code.Length != 6 || !code.All(char.IsAsciiDigit))
            {
                problem = L("Der Code hat 6 Ziffern.", "The code has 6 digits.");
                continue;
            }
            try
            {
                var session = await _create(miner.IpAddress, code);
                _sessions[miner.Id] = session;
                Persist();
                return session.Token;
            }
            catch (MinerApiException e) when (e.Status == 405)
            {
                return null;
            }
            catch (MinerApiException e) when (e.IsOtpWrongCode)
            {
                problem = L("Der Code stimmt nicht. Warte auf den nächsten und versuche es noch einmal.",
                            "That code is wrong. Wait for the next one and try again.");
            }
        }
    }

    /// <summary>
    /// Runs a write. <paramref name="interactive"/> may ask for a code; otherwise only a stored session is
    /// used and a refused one is dropped, so the next attempt does not cost another failed try.
    /// </summary>
    public async Task PerformAsync(MinerDevice miner, bool interactive, Func<string?, Task> write)
    {
        string? token = null;
        if (miner.RequiresOtp)
        {
            token = interactive ? await AuthorizeAsync(miner) : Token(miner.Id);
            if (token == null && !interactive) throw OtpException.CodeNeeded();
        }
        try
        {
            await write(token);
        }
        catch (MinerApiException e) when (e.IsOtpRefused)
        {
            Invalidate(miner.Id);
            if (!interactive) throw;
            await write(await AuthorizeAsync(miner));
        }
    }

#pragma warning disable CA1416 // The app only runs on Windows.
    private static byte[] SealForUser(byte[] plain) => ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var stored = JsonSerializer.Deserialize<Dictionary<Guid, Stored>>(File.ReadAllText(_path)) ?? [];
            foreach (var (id, entry) in stored)
            {
                var token = ProtectedData.Unprotect(Convert.FromBase64String(entry.ProtectedToken), null, DataProtectionScope.CurrentUser);
                _sessions[id] = new OtpSession(Encoding.UTF8.GetString(token), entry.ExpiresUtc);
            }
        }
        catch (Exception e) when (e is JsonException or IOException or CryptographicException or FormatException)
        {
            // Unreadable: the miners simply ask for a code again.
        }
    }

    private void Persist()
    {
        try
        {
            var stored = _sessions.ToDictionary(p => p.Key, p => new Stored(
                Convert.ToBase64String(_seal(Encoding.UTF8.GetBytes(p.Value.Token))), p.Value.ExpiresUtc));
            File.WriteAllText(_path, JsonSerializer.Serialize(stored));
        }
        catch (Exception e) when (e is IOException or CryptographicException)
        {
            // Stays in memory; the next change tries again.
            DiagnosticLog.Shared.Write("otp", $"not saved: {e.GetType().Name}");
        }
        Changed?.Invoke();
    }
#pragma warning restore CA1416
}
