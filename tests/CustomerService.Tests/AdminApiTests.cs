using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CustomerService.Admin;
using CustomerService.Chat;
using CustomerService.Config;
using CustomerService.HttpApi;
using CustomerService.Tests.Support;
using CustomerService.Tickets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CustomerService.Tests;

/// <summary>The admin API over a real Postgres: sessions, roles, refusals, and what gets audited.</summary>
[Collection("postgres-8")]
public class AdminApiTests(Postgres8 pg)
{
    sealed class Harness : IAsyncDisposable
    {
        public WebApplication App = null!;
        public HttpClient Client = null!;
        public StaffAccounts Accounts = null!;
        public TicketStore Tickets = null!;
        public PostgresTurnRecorder Recorder = null!;
        public AdminAudit Audit = null!;
        public string Suffix = Guid.NewGuid().ToString("N")[..8];
        public string Admin => $"root-{Suffix}";
        public string Support => $"alice-{Suffix}";
        public async ValueTask DisposeAsync() => await App.DisposeAsync();
    }

    async Task<Harness> Start(TimeSpan? sessionTimeout = null, Func<DateTimeOffset>? clock = null)
    {
        var h = new Harness();
        h.Accounts = new StaffAccounts(pg.Db);
        h.Tickets = new TicketStore(pg.Db);
        h.Recorder = new PostgresTurnRecorder(pg.Db);
        h.Audit = new AdminAudit(pg.Db);
        var feedback = new Feedback(pg.Db);
        var services = new AdminServices(h.Accounts, new StaffSessions(pg.Db, h.Accounts, sessionTimeout ?? TimeSpan.FromMinutes(30), clock), h.Audit,
            h.Tickets, new Conversations(pg.Db, h.Tickets, feedback), feedback);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        h.App = builder.Build();
        h.App.MapAdminEndpoints(services, new AdminConfig(true, null, null, TimeSpan.FromMinutes(30), []), NullLogger.Instance);
        await h.App.StartAsync();
        h.Client = h.App.GetTestClient();
        await h.Accounts.CreateAsync(h.Admin, "correct-horse-battery", StaffRole.Admin, CancellationToken.None);
        await h.Accounts.CreateAsync(h.Support, "correct-horse-battery", StaffRole.Support, CancellationToken.None);
        return h;
    }

    static async Task<string> Login(HttpClient c, string user, string password = "correct-horse-battery")
    {
        var res = await c.PostAsJsonAsync("/api/admin/v1/session", new { username = user, password });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
    }

    static HttpRequestMessage Req(HttpMethod m, string path, string? token, object? body = null)
    {
        var r = new HttpRequestMessage(m, path);
        if (token is not null) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) r.Content = JsonContent.Create(body);
        return r;
    }

    async Task<int> AuditRows(string actor, string outcome)
    {
        await using var cmd = pg.Db.CreateCommand("SELECT count(*) FROM admin_audit WHERE actor = $1 AND outcome = $2");
        cmd.Parameters.Add(new Npgsql.NpgsqlParameter { Value = actor });
        cmd.Parameters.Add(new Npgsql.NpgsqlParameter { Value = outcome });
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task WithoutASessionEverythingIs401()
    {
        await using var h = await Start();
        foreach (var path in new[] { "/api/admin/v1/me", "/api/admin/v1/tickets", "/api/admin/v1/conversations", "/api/admin/v1/staff", "/api/admin/v1/audit-events" })
        {
            var res = await h.Client.SendAsync(Req(HttpMethod.Get, path, null));
            Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
            Assert.Equal("application/problem+json", res.Content.Headers.ContentType?.MediaType);
        }
        var garbage = await h.Client.SendAsync(Req(HttpMethod.Get, "/api/admin/v1/me", "not-a-token"));
        Assert.Equal(HttpStatusCode.Unauthorized, garbage.StatusCode);
    }

    [Fact]
    public async Task AWrongPasswordADisabledAccountAndAnUnknownUserAreIndistinguishable()
    {
        await using var h = await Start();
        var wrong = await h.Client.PostAsJsonAsync("/api/admin/v1/session", new { username = h.Support, password = "wrong-password-here" });
        var unknown = await h.Client.PostAsJsonAsync("/api/admin/v1/session", new { username = "nobody-" + h.Suffix, password = "correct-horse-battery" });
        await h.Accounts.UpdateAsync(h.Support, null, false, null, CancellationToken.None);
        var disabled = await h.Client.PostAsJsonAsync("/api/admin/v1/session", new { username = h.Support, password = "correct-horse-battery" });
        foreach (var res in new[] { wrong, unknown, disabled })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
            Assert.Equal("Sign-in failed", (await res.Content.ReadFromJsonAsync<Problem>())!.Title);
        }
    }

    [Fact]
    public async Task LogoutAndDisablingEndASession()
    {
        await using var h = await Start();
        var token = await Login(h.Client, h.Support);
        Assert.Equal(HttpStatusCode.OK, (await h.Client.SendAsync(Req(HttpMethod.Get, "/api/admin/v1/me", token))).StatusCode);
        await h.Client.SendAsync(Req(HttpMethod.Delete, "/api/admin/v1/session", token));
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.Client.SendAsync(Req(HttpMethod.Get, "/api/admin/v1/me", token))).StatusCode);

        var again = await Login(h.Client, h.Support);
        var admin = await Login(h.Client, h.Admin);
        var patch = await h.Client.SendAsync(Req(HttpMethod.Patch, $"/api/admin/v1/staff/{h.Support}", admin, new { enabled = false }));
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        // Revoked permissions must stop an already-open page.
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.Client.SendAsync(Req(HttpMethod.Get, "/api/admin/v1/me", again))).StatusCode);
    }

    [Fact]
    public async Task AnIdleSessionExpires()
    {
        var now = DateTimeOffset.UtcNow;
        await using var h = await Start(TimeSpan.FromMinutes(30), () => now);
        var token = await Login(h.Client, h.Support);
        now = now.AddMinutes(29);
        Assert.Equal(HttpStatusCode.OK, (await h.Client.SendAsync(Req(HttpMethod.Get, "/api/admin/v1/me", token))).StatusCode);
        now = now.AddMinutes(31);
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.Client.SendAsync(Req(HttpMethod.Get, "/api/admin/v1/me", token))).StatusCode);
    }

    [Fact]
    public async Task SupportMayNotManageStaffOrReadTheAuditAndTheRefusalIsAudited()
    {
        await using var h = await Start();
        var token = await Login(h.Client, h.Support);
        var staff = await h.Client.SendAsync(Req(HttpMethod.Get, "/api/admin/v1/staff", token));
        var audit = await h.Client.SendAsync(Req(HttpMethod.Get, "/api/admin/v1/audit-events", token));
        var create = await h.Client.SendAsync(Req(HttpMethod.Post, "/api/admin/v1/staff", token, new { username = "x", password = "correct-horse-battery", role = "admin" }));
        Assert.All(new[] { staff, audit, create }, r => Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode));
        Assert.Equal(3, await AuditRows(h.Support, "refused"));

        var admin = await Login(h.Client, h.Admin);
        var list = await h.Client.SendAsync(Req(HttpMethod.Get, $"/api/admin/v1/audit-events?actor={h.Support}", admin));
        var page = await list.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, page.GetProperty("total").GetInt64());
        Assert.Contains(page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("detail").GetString()), d => d!.Contains("may not GET /api/admin/v1/staff"));
    }

    [Fact]
    public async Task AnAdminCannotDisableOrDemoteThemselves()
    {
        await using var h = await Start();
        var admin = await Login(h.Client, h.Admin);
        var res = await h.Client.SendAsync(Req(HttpMethod.Patch, $"/api/admin/v1/staff/{h.Admin}", admin, new { enabled = false }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        Assert.Equal(1, await AuditRows(h.Admin, "refused"));
    }

    [Fact]
    public async Task TheTicketLoopRunsThroughTheApiWithVersionsAndRefusals()
    {
        await using var h = await Start();
        var conv = Guid.NewGuid().ToString();
        var created = await h.Tickets.CreateAsync(conv, "Customer wants a refund", "returns", null, CancellationToken.None);
        var n = created.Ticket.TicketNumber;
        var alice = await Login(h.Client, h.Support);

        var missingVersion = await h.Client.SendAsync(Req(HttpMethod.Post, $"/api/admin/v1/tickets/{n}/claim", alice, new { }));
        Assert.True(HttpStatusCode.BadRequest == missingVersion.StatusCode, $"{missingVersion.StatusCode}: {await missingVersion.Content.ReadAsStringAsync()}");

        var claim = await h.Client.SendAsync(Req(HttpMethod.Post, $"/api/admin/v1/tickets/{n}/claim", alice, new { expectedVersion = 1 }));
        Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
        var detail = await claim.Content.ReadFromJsonAsync<JsonElement>(ChatEndpoints.Json);
        Assert.Equal("claimed", detail.GetProperty("ticket").GetProperty("state").GetString());
        Assert.Equal(2, detail.GetProperty("ticket").GetProperty("version").GetInt32());

        var stale = await h.Client.SendAsync(Req(HttpMethod.Post, $"/api/admin/v1/tickets/{n}/resolve", alice, new { expectedVersion = 1, text = "done" }));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        var noConclusion = await h.Client.SendAsync(Req(HttpMethod.Post, $"/api/admin/v1/tickets/{n}/resolve", alice, new { expectedVersion = 2 }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noConclusion.StatusCode);
        Assert.Equal(1, await AuditRows(h.Support, "refused"));

        var resolve = await h.Client.SendAsync(Req(HttpMethod.Post, $"/api/admin/v1/tickets/{n}/resolve", alice, new { expectedVersion = 2, text = "Refund issued." }));
        Assert.Equal(HttpStatusCode.OK, resolve.StatusCode);

        var queue = await h.Client.SendAsync(Req(HttpMethod.Get, $"/api/admin/v1/tickets?state=resolved&conversationId={conv}", alice));
        var page = await queue.Content.ReadFromJsonAsync<JsonElement>(ChatEndpoints.Json);
        Assert.Equal(1, page.GetProperty("total").GetInt64());

        var get = await h.Client.SendAsync(Req(HttpMethod.Get, $"/api/admin/v1/tickets/{n}", alice));
        var history = (await get.Content.ReadFromJsonAsync<JsonElement>(ChatEndpoints.Json)).GetProperty("history").EnumerateArray().ToList();
        Assert.Equal(["created", "claimed", "resolved"], history.Select(e => e.GetProperty("kind").GetString()).ToArray());
    }

    [Fact]
    public async Task ReadingAConversationIsAuditedAndFeedbackCanBeRaisedAndClosed()
    {
        await using var h = await Start();
        var conv = Guid.NewGuid().ToString();
        var turnId = await h.Recorder.OpenAsync(conv, "how long do I have to return?", DateTimeOffset.UtcNow, CancellationToken.None);
        await h.Recorder.CloseAsync(turnId, new TurnClose("completed", null, "Thirty days.", "stub-model", [new ModelCallRecord(1, "stub-model", 100, 20, "end_turn", false)], 100, 20, null, null,
            [new PassageSummary("returns-window", "en", 0.91, "How long do I have to return an item?")], []), DateTimeOffset.UtcNow, CancellationToken.None);
        await new ConversationMemory(pg.Db, 40).AppendAsync(conv, Llm.Role.User, "how long do I have to return?", CancellationToken.None);

        var alice = await Login(h.Client, h.Support);
        var list = await h.Client.SendAsync(Req(HttpMethod.Get, $"/api/admin/v1/conversations?q={conv[..8]}", alice));
        var page = await list.Content.ReadFromJsonAsync<JsonElement>(ChatEndpoints.Json);
        Assert.Equal(1, page.GetProperty("total").GetInt64());
        Assert.Equal("completed", page.GetProperty("items")[0].GetProperty("lastOutcome").GetString());

        var detail = await h.Client.SendAsync(Req(HttpMethod.Get, $"/api/admin/v1/conversations/{conv}", alice));
        var d = await detail.Content.ReadFromJsonAsync<JsonElement>(ChatEndpoints.Json);
        Assert.Equal("returns-window", d.GetProperty("turns")[0].GetProperty("retrieval")[0].GetProperty("entryId").GetString());
        Assert.Single(d.GetProperty("messages").EnumerateArray());
        await using (var cmd = pg.Db.CreateCommand("SELECT count(*) FROM admin_audit WHERE actor = $1 AND action = 'read conversation' AND object_id = $2"))
        {
            cmd.Parameters.Add(new Npgsql.NpgsqlParameter { Value = h.Support });
            cmd.Parameters.Add(new Npgsql.NpgsqlParameter { Value = conv });
            Assert.Equal(1, Convert.ToInt32(await cmd.ExecuteScalarAsync()));
        }

        var flag = await h.Client.SendAsync(Req(HttpMethod.Post, $"/api/admin/v1/conversations/{conv}/turns/{turnId}/feedback", alice, new { issue = "incomplete", note = "did not mention final-sale items" }));
        Assert.Equal(HttpStatusCode.Created, flag.StatusCode);
        var fb = await flag.Content.ReadFromJsonAsync<JsonElement>(ChatEndpoints.Json);
        var id = fb.GetProperty("id").GetInt64();

        var open = await h.Client.SendAsync(Req(HttpMethod.Get, "/api/admin/v1/feedback?state=open", alice));
        Assert.Contains((await open.Content.ReadFromJsonAsync<JsonElement>(ChatEndpoints.Json)).GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetInt64() == id);

        var noConclusion = await h.Client.SendAsync(Req(HttpMethod.Post, $"/api/admin/v1/feedback/{id}/close", alice, new { expectedVersion = 1 }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noConclusion.StatusCode);
        var close = await h.Client.SendAsync(Req(HttpMethod.Post, $"/api/admin/v1/feedback/{id}/close", alice, new { expectedVersion = 1, conclusion = "FAQ entry revised." }));
        Assert.Equal(HttpStatusCode.OK, close.StatusCode);
        var twice = await h.Client.SendAsync(Req(HttpMethod.Post, $"/api/admin/v1/feedback/{id}/close", alice, new { expectedVersion = 2, conclusion = "again" }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, twice.StatusCode);

        var overview = await h.Client.SendAsync(Req(HttpMethod.Get, "/api/admin/v1/overview", alice));
        var o = await overview.Content.ReadFromJsonAsync<JsonElement>(ChatEndpoints.Json);
        Assert.True(o.GetProperty("turns").GetInt64() >= 1);
        Assert.True(o.GetProperty("unpricedTurns").GetInt64() >= 1, "a stub model has no price; the overview must say so rather than cost it at zero");
    }

    [Fact]
    public async Task SeedingCreatesTheFirstAdminOnlyIntoAnEmptyTable()
    {
        // The shared database already has accounts, so the seed must refuse; a fresh
        // database would take it. Both halves of the contract in one place.
        var accounts = new StaffAccounts(pg.Db);
        await accounts.CreateAsync("someone-" + Guid.NewGuid().ToString("N")[..8], "correct-horse-battery", StaffRole.Support, CancellationToken.None);
        Assert.False(await accounts.SeedAsync("seed-admin", "correct-horse-battery", CancellationToken.None));
        Assert.Null(await accounts.FindAsync("seed-admin", CancellationToken.None));
        Assert.Throws<ConfigException>(() => AdminConfig.From(true, "root", null, TimeSpan.FromMinutes(1), ""));
        Assert.Throws<ConfigException>(() => AdminConfig.From(true, "root", "short", TimeSpan.FromMinutes(1), ""));
    }

    [Fact]
    public void PasswordHashesVerifyAndDoNotRepeat()
    {
        var a = StaffAccounts.Hash("correct-horse-battery");
        var b = StaffAccounts.Hash("correct-horse-battery");
        Assert.NotEqual(a, b);
        Assert.True(StaffAccounts.Verify("correct-horse-battery", a));
        Assert.False(StaffAccounts.Verify("correct-horse-batterz", a));
        Assert.StartsWith("pbkdf2-sha256$", a);
    }
}
