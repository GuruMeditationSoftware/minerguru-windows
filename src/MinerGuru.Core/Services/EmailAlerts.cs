using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using static MinerGuru.Core.Lang;

namespace MinerGuru.Core.Services;

public sealed class EmailSettings
{
    public bool IsEnabled { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = SmtpSecurity.StartTls.DefaultPort();
    public SmtpSecurity Security { get; set; } = SmtpSecurity.StartTls;
    public string Username { get; set; } = "";
    public string Sender { get; set; } = "";
    /// <summary>Several addresses separated by commas.</summary>
    public string Recipients { get; set; } = "";
    /// <summary>The password, encrypted for this Windows user; a copy of the file is of no use elsewhere.</summary>
    public string? ProtectedPassword { get; set; }

    public List<string> RecipientList =>
        Recipients.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    public bool IsComplete => Host.Trim().Length > 0 && Port is >= 1 and <= 65535 && Sender.Contains('@') && RecipientList.Count > 0;

    public EmailSettings Copy() => (EmailSettings)MemberwiseClone();
}

/// <summary>Sends alerts by mail on top of the notification. Settings are in <c>email.json</c>.</summary>
public sealed class EmailAlerts
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path;
    private readonly Func<SmtpMessage, SmtpServer, Task> _send;
    private readonly Func<byte[], byte[]> _seal;

    /// <param name="seal">Encrypts the password for the disk, by default for the current Windows user.</param>
    public EmailAlerts(string dataDirectory, Func<SmtpMessage, SmtpServer, Task>? send = null, Func<byte[], byte[]>? seal = null)
    {
        _path = Path.Combine(dataDirectory, "email.json");
        _send = send ?? SmtpClient.SendAsync;
        _seal = seal ?? SealForUser;
        try
        {
            if (File.Exists(_path)) Settings = JsonSerializer.Deserialize<EmailSettings>(File.ReadAllText(_path)) ?? Settings;
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            // Unreadable: start with the defaults; the next save replaces the file.
        }
    }

    public EmailSettings Settings { get; private set; } = new();

    public bool HasPassword => Settings.ProtectedPassword != null;

    /// <param name="settings">The new settings.</param>
    /// <param name="password">New password, <c>null</c> to keep the stored one, empty to remove it.</param>
    /// <returns><c>false</c> when the password could not be encrypted: nothing changed, and it is never stored in the clear.</returns>
    public bool Save(EmailSettings settings, string? password)
    {
        try
        {
            settings.ProtectedPassword = password switch
            {
                null => Settings.ProtectedPassword,
                "" => null,
                _ => Convert.ToBase64String(_seal(Encoding.UTF8.GetBytes(password))),
            };
        }
        catch (CryptographicException e)
        {
            DiagnosticLog.Shared.Write("mail", $"password not saved: {e.GetType().Name}");
            return false;
        }
        Settings = settings;
        try { File.WriteAllText(_path, JsonSerializer.Serialize(Settings, Options)); }
        catch (IOException) { /* Stays in memory; the next save tries again. */ }
        return true;
    }

    /// <summary>Throws what went wrong, for the test button.</summary>
    public Task SendTestAsync(EmailSettings settings, string? password) => SendAsync(settings, password ?? Unprotect(settings.ProtectedPassword ?? Settings.ProtectedPassword),
        L("MinerGuru: Test-Mail", "MinerGuru: test mail"),
        L("Diese Test-Mail zeigt: Warnungen von MinerGuru kommen bei dir an.", "This test mail shows that alerts from MinerGuru reach you."));

    /// <summary>A failed mail is logged, the alert itself stays delivered.</summary>
    public async Task DeliverAsync(IReadOnlyCollection<AlertEvent> events)
    {
        if (events.Count == 0 || !Settings.IsEnabled || !Settings.IsComplete) return;
        var password = Unprotect(Settings.ProtectedPassword);
        foreach (var alert in events)
        {
            try
            {
                await SendAsync(Settings, password, $"MinerGuru: {alert.Title}", alert.Body);
            }
            catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or OperationCanceledException or System.Security.Authentication.AuthenticationException)
            {
                Trace.WriteLine($"Warn-Mail nicht versendet: {e.Message}");
            }
        }
    }

    private Task SendAsync(EmailSettings settings, string password, string subject, string body) =>
        _send(new SmtpMessage(settings.Sender.Trim(), settings.RecipientList, subject, body),
            new SmtpServer(settings.Host.Trim(), settings.Port, settings.Security, settings.Username.Trim(), password));

#pragma warning disable CA1416 // The app only runs on Windows.
    private static byte[] SealForUser(byte[] plain) => ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);

    private static string Unprotect(string? sealedPassword)
    {
        if (sealedPassword == null) return "";
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(sealedPassword), null, DataProtectionScope.CurrentUser));
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return "";
        }
    }
#pragma warning restore CA1416
}
