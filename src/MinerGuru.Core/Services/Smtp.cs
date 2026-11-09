using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using static MinerGuru.Core.Lang;

namespace MinerGuru.Core.Services;

public enum SmtpSecurity
{
    /// <summary>Plain connection upgraded with STARTTLS, usually port 587.</summary>
    StartTls,
    /// <summary>TLS from the first byte, usually port 465.</summary>
    Tls,
    /// <summary>No encryption, only for a relay in the own network.</summary>
    None,
}

public static class SmtpSecurityText
{
    public static int DefaultPort(this SmtpSecurity security) => security switch
    {
        SmtpSecurity.StartTls => 587,
        SmtpSecurity.Tls => 465,
        _ => 25,
    };

    public static string Title(this SmtpSecurity security) => security switch
    {
        SmtpSecurity.StartTls => "STARTTLS",
        SmtpSecurity.Tls => "SSL/TLS",
        _ => L("Keine", "None"),
    };
}

public sealed class SmtpException(string message) : IOException(message);

/// <summary>A plain-text mail, encoded for the DATA phase.</summary>
public sealed record SmtpMessage(string From, IReadOnlyList<string> To, string Subject, string Body)
{
    public DateTime Date { get; init; } = DateTime.UtcNow;
    public string MessageId { get; init; } = Guid.NewGuid().ToString();

    /// <summary>Headers and a base64 body, lines ending in CRLF, ready to go between DATA and the final dot.</summary>
    public string Encoded()
    {
        var at = From.LastIndexOf('@');
        var domain = at >= 0 ? From[(at + 1)..] : "minerguru.local";
        var subject = Subject.All(c => c is >= ' ' and <= '~') ? Subject : $"=?UTF-8?B?{Base64(Subject)}?=";
        var body = Convert.ToBase64String(Encoding.UTF8.GetBytes(Body));
        var lines = Enumerable.Range(0, (body.Length + 75) / 76).Select(i => body.Substring(i * 76, Math.Min(76, body.Length - i * 76)));
        return string.Join("\r\n",
            $"From: {From}", $"To: {string.Join(", ", To)}", $"Subject: {subject}",
            $"Date: {Date.ToUniversalTime().ToString("ddd, dd MMM yyyy HH:mm:ss '+0000'", System.Globalization.CultureInfo.InvariantCulture)}",
            $"Message-ID: <{MessageId}@{domain}>", "MIME-Version: 1.0", "Content-Type: text/plain; charset=utf-8",
            "Content-Transfer-Encoding: base64", "", string.Join("\r\n", lines));
    }

    internal static string Base64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
}

/// <summary>What the dialogue wants next: a line to send, the TLS upgrade, or nothing more.</summary>
public abstract record SmtpAction
{
    public sealed record Send(string Line) : SmtpAction;
    public sealed record StartTls : SmtpAction;
    public sealed record Done : SmtpAction;
}

/// <summary>The SMTP dialogue without any networking: feed it the server's replies, it says what to send next.</summary>
public sealed class SmtpDialogue(SmtpMessage message, string username, string password, bool useStartTls, string clientName = "minerguru.local")
{
    private enum Step { Greeting, Ehlo, StartTls, EhloAfterTls, Auth, User, Password, MailFrom, RcptTo, Data, Body, Quit }

    private Step _step = Step.Greeting;
    private int _recipient;

    public string Ehlo => $"EHLO {clientName}";

    /// <summary>Takes the code and text of a reply, returns the next thing to do.</summary>
    public SmtpAction Next(int code, string text)
    {
        switch (_step)
        {
            case Step.Greeting:
                Expect(code, 220, text);
                _step = Step.Ehlo;
                return new SmtpAction.Send(Ehlo);
            case Step.Ehlo:
                Expect(code, 250, text);
                if (!useStartTls) return Authenticate();
                if (!text.Contains("STARTTLS", StringComparison.OrdinalIgnoreCase))
                    throw new SmtpException(L("Der Mailserver bietet kein STARTTLS an.", "The mail server does not offer STARTTLS."));
                _step = Step.StartTls;
                return new SmtpAction.Send("STARTTLS");
            case Step.StartTls:
                Expect(code, 220, text);
                _step = Step.EhloAfterTls;
                return new SmtpAction.StartTls();
            case Step.EhloAfterTls:
                Expect(code, 250, text);
                return Authenticate();
            case Step.Auth:
                Expect(code, 334, text);
                _step = Step.User;
                return new SmtpAction.Send(SmtpMessage.Base64(username));
            case Step.User:
                Expect(code, 334, text);
                _step = Step.Password;
                return new SmtpAction.Send(SmtpMessage.Base64(password));
            case Step.Password:
                Expect(code, 235, text);
                return MailFrom();
            case Step.MailFrom:
                Expect(code, 250, text);
                _step = Step.RcptTo;
                return new SmtpAction.Send($"RCPT TO:<{message.To[_recipient++]}>");
            case Step.RcptTo:
                Expect(code, 250, text);
                if (_recipient < message.To.Count) return new SmtpAction.Send($"RCPT TO:<{message.To[_recipient++]}>");
                _step = Step.Data;
                return new SmtpAction.Send("DATA");
            case Step.Data:
                Expect(code, 354, text);
                _step = Step.Body;
                return new SmtpAction.Send(DotStuffed(message.Encoded()) + "\r\n.");
            case Step.Body:
                Expect(code, 250, text);
                _step = Step.Quit;
                return new SmtpAction.Send("QUIT");
            default:
                return new SmtpAction.Done();
        }
    }

    private SmtpAction Authenticate()
    {
        if (username.Length == 0) return MailFrom();
        _step = Step.Auth;
        return new SmtpAction.Send("AUTH LOGIN");
    }

    private SmtpAction MailFrom()
    {
        _step = Step.MailFrom;
        return new SmtpAction.Send($"MAIL FROM:<{message.From}>");
    }

    private static void Expect(int code, int wanted, string text)
    {
        if (code != wanted) throw new SmtpException(L($"Mailserver: {text}", $"Mail server: {text}"));
    }

    /// <summary>Lines that start with a dot get a second one, so they do not end the message.</summary>
    public static string DotStuffed(string text) =>
        string.Join("\r\n", text.Split("\r\n").Select(line => line.StartsWith('.') ? "." + line : line));
}

public sealed record SmtpServer(string Host, int Port, SmtpSecurity Security, string Username, string Password);

public static class SmtpClient
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public static async Task SendAsync(SmtpMessage message, SmtpServer server)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        var token = timeout.Token;
        using var client = new TcpClient();
        await client.ConnectAsync(server.Host, server.Port, token);
        Stream stream = client.GetStream();
        if (server.Security == SmtpSecurity.Tls) stream = await UpgradeAsync(stream, server.Host, token);
        var dialogue = new SmtpDialogue(message, server.Username, server.Password, server.Security == SmtpSecurity.StartTls);
        var reader = new StreamReader(stream, Encoding.UTF8);
        while (true)
        {
            var (code, text) = await ReadReplyAsync(reader, token);
            switch (dialogue.Next(code, text))
            {
                case SmtpAction.Send send:
                    await WriteLineAsync(stream, send.Line, token);
                    break;
                case SmtpAction.StartTls:
                    stream = await UpgradeAsync(stream, server.Host, token);
                    reader = new StreamReader(stream, Encoding.UTF8);
                    await WriteLineAsync(stream, dialogue.Ehlo, token);
                    break;
                default:
                    return;
            }
        }
    }

    private static async Task<Stream> UpgradeAsync(Stream stream, string host, CancellationToken token)
    {
        var tls = new SslStream(stream);
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, token);
        return tls;
    }

    private static async Task WriteLineAsync(Stream stream, string text, CancellationToken token)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes(text + "\r\n"), token);
        await stream.FlushAsync(token);
    }

    /// <summary>Reads one reply; lines with <c>-</c> after the code continue it.</summary>
    private static async Task<(int Code, string Text)> ReadReplyAsync(StreamReader reader, CancellationToken token)
    {
        var text = new StringBuilder();
        while (true)
        {
            var line = await reader.ReadLineAsync(token)
                ?? throw new SmtpException(L("Der Mailserver hat die Verbindung getrennt.", "The mail server closed the connection."));
            text.AppendLine(line);
            if (line.Length < 3 || !int.TryParse(line[..3], out var code))
                throw new SmtpException(L("Unerwartete Antwort vom Mailserver.", "Unexpected reply from the mail server."));
            if (line.Length == 3 || line[3] != '-') return (code, text.ToString().Trim());
        }
    }
}
