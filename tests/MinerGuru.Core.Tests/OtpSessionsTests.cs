using System.Net;
using System.Text.Json.Nodes;
using MinerGuru.Core.Models;
using MinerGuru.Core.Services;
using Xunit;

namespace MinerGuru.Core.Tests;

public class OtpSessionsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private DateTime _now = new(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
    private readonly List<string?> _problems = [];
    private readonly MinerApiException _refused = MinerApiException.FromResponse(401, "OTP/Session required");

    public OtpSessionsTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static MinerDevice Miner(bool otp = true) =>
        new(new PersistedMinerConfig { IpAddress = "192.0.2.1" }, ConnectionState.Online)
        {
            LatestInfo = MinerSystemInfo.Parse(new JsonObject { ["otp"] = otp, ["power"] = 15.0, ["frequency"] = 600, ["coreVoltage"] = 1250, ["autofanspeed"] = 1, ["version"] = "v1.1.0", ["boardVersion"] = "501" }),
        };

    private OtpSessions Sessions(params string?[] answers) => Sessions(null, answers);

    private OtpSessions Sessions(Func<string, string, Task<OtpSession>>? create, params string?[] answers)
    {
        var queue = new Queue<string?>(answers);
        return new OtpSessions(_folder, create ?? ((_, code) => Task.FromResult(new OtpSession($"t{code}", _now.AddDays(1)))), () => _now)
        {
            Prompt = (_, problem) =>
            {
                _problems.Add(problem);
                return Task.FromResult(queue.Count > 0 ? queue.Dequeue() : null);
            },
        };
    }

    [Fact]
    public void ExpiryComesFromTheDurationNotTheMinersClock()
    {
        var session = OtpSession.Parse("""{"token":"abc","ttlMs":86400000,"expiresAt":5}""", _now);
        Assert.Equal("abc", session.Token);
        Assert.Equal(_now.AddDays(1), session.ExpiresUtc);
        Assert.True(session.IsUsable(_now));
        Assert.False(session.IsUsable(_now.AddDays(1).AddSeconds(-30)));
    }

    [Fact]
    public void RecognisesTheAnswersOfTheFirmware()
    {
        Assert.True(_refused.IsOtpRefused);
        var blocked = MinerApiException.FromResponse(401, "blocked for 5 minutes");
        Assert.True(blocked.IsOtpBlocked);
        Assert.False(blocked.IsOtpRefused);
        Assert.False(MinerApiException.FromResponse(401, "Unauthorized").IsOtpRefused);
        Assert.True(MinerApiException.FromResponse(401, "Invalid TOTP").IsOtpWrongCode);
    }

    [Fact]
    public async Task MinerWithoutOtpNeverAsks()
    {
        string? sent = "x";
        await Sessions().PerformAsync(Miner(otp: false), true, t => { sent = t; return Task.CompletedTask; });
        Assert.Null(sent);
        Assert.Empty(_problems);
    }

    [Fact]
    public async Task AsksOnceAndReusesTheSessionEvenAfterARestart()
    {
        var miner = Miner();
        var otp = Sessions("123456");
        string? sent = null;
        await otp.PerformAsync(miner, true, t => { sent = t; return Task.CompletedTask; });
        await otp.PerformAsync(miner, true, t => { sent = t; return Task.CompletedTask; });
        Assert.Equal("t123456", sent);
        Assert.Single(_problems);
        Assert.True(Sessions().CanWriteUnattended(miner));
        Assert.DoesNotContain("t123456", File.ReadAllText(Path.Combine(_folder, "otp.json")));
    }

    [Fact]
    public async Task AsksAgainAfterAMalformedOrWrongCode()
    {
        var otp = Sessions((_, code) => code == "123456"
            ? Task.FromResult(new OtpSession("ok", _now.AddDays(1)))
            : throw MinerApiException.FromResponse(401, "Invalid TOTP"), "12", "111111", " 123456 ");
        Assert.Equal("ok", await otp.AuthorizeAsync(Miner()));
        Assert.Equal(3, _problems.Count);
        Assert.Null(_problems[0]);
        Assert.NotNull(_problems[1]);
        Assert.NotNull(_problems[2]);
    }

    [Fact]
    public async Task CancellingWritesNothing()
    {
        var writes = 0;
        await Assert.ThrowsAsync<OtpException>(() => Sessions().PerformAsync(Miner(), true, _ => { writes++; return Task.CompletedTask; }));
        Assert.Equal(0, writes);
    }

    [Fact]
    public async Task MinerThatTurnedOtpOffGoesOn()
    {
        var otp = Sessions((_, _) => throw MinerApiException.FromResponse(405, ""), "123456");
        Assert.Null(await otp.AuthorizeAsync(Miner()));
    }

    [Fact]
    public async Task ARefusedSessionIsReplacedOnce()
    {
        var otp = Sessions("123456", "654321");
        var writes = 0;
        string? sent = null;
        await otp.PerformAsync(Miner(), true, t =>
        {
            if (++writes == 1) throw _refused;
            sent = t;
            return Task.CompletedTask;
        });
        Assert.Equal("t654321", sent);
        Assert.Equal(2, writes);
    }

    [Fact]
    public async Task UnattendedWriteWithoutSessionSendsNothing()
    {
        var writes = 0;
        await Assert.ThrowsAsync<OtpException>(() => Sessions("123456").PerformAsync(Miner(), false, _ => { writes++; return Task.CompletedTask; }));
        Assert.Equal(0, writes);
        Assert.Empty(_problems);
    }

    [Fact]
    public async Task UnattendedWriteDropsARefusedSession()
    {
        var miner = Miner();
        var otp = Sessions("123456");
        await otp.AuthorizeAsync(miner);
        await Assert.ThrowsAsync<MinerApiException>(() => otp.PerformAsync(miner, false, _ => throw _refused));
        Assert.False(otp.CanWriteUnattended(miner));
    }

    [Fact]
    public async Task ExpiredSessionDoesNotCountForSchedules()
    {
        var miner = Miner();
        var otp = Sessions("123456");
        await otp.AuthorizeAsync(miner);
        Assert.True(otp.CanWriteUnattended(miner));
        _now = _now.AddDays(1);
        Assert.False(otp.CanWriteUnattended(miner));
    }

    private sealed class Recorder : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add($"{request.Method} {request.RequestUri!.AbsolutePath} {string.Join(',', request.Headers.Where(h => h.Key.StartsWith("X-")).Select(h => h.Key))}");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    [Fact]
    public async Task ASchedulePlansNoRequestWithoutASessionAndUsesOneOnceThereIs()
    {
        var http = new Recorder();
        var miner = Miner();
        var local = new DateTime(2026, 11, 4, 12, 0, 0);
        var otp = Sessions("123456");
        var power = new PowerStore(_folder, new MinerApiClient(http), () => [miner], () => local, otp) { RunsSchedules = true };
        power.SaveRule(new ScheduleRule { Action = PowerAction.NightMode, Trigger = ScheduleTrigger.At(new(WeekdayOrder.All, 0, 24 * 60)) });
        await power.Evaluation;
        Assert.Empty(http.Calls);
        await otp.AuthorizeAsync(miner);
        await power.EvaluateAsync();
        Assert.Contains("PATCH /api/system X-OTP-Session", http.Calls);
    }

    [Fact]
    public async Task AFailedEncryptionKeepsTheSessionInMemory()
    {
        var changes = 0;
        var otp = new OtpSessions(_folder, (_, code) => Task.FromResult(new OtpSession($"t{code}", _now.AddDays(1))), () => _now,
            _ => throw new System.Security.Cryptography.CryptographicException("refused")) { Prompt = (_, _) => Task.FromResult<string?>("123456") };
        otp.Changed += () => changes++;
        var miner = Miner();

        Assert.Equal("t123456", await otp.AuthorizeAsync(miner));
        Assert.Equal("t123456", otp.Token(miner.Id));
        Assert.False(File.Exists(Path.Combine(_folder, "otp.json")));
        otp.Invalidate(miner.Id);
        Assert.Equal(2, changes);
    }
}
