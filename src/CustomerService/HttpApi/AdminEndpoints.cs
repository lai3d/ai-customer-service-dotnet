using System.Text.Json;
using CustomerService.Admin;
using CustomerService.Config;
using CustomerService.Tickets;

namespace CustomerService.HttpApi;

/// <summary>Everything the admin API needs. Built only when the admin is enabled.</summary>
public sealed record AdminServices(StaffAccounts Accounts, StaffSessions Sessions, AdminAudit Audit, TicketStore Tickets, Conversations Conversations, Feedback Feedback);

/// <summary>
/// The management API for the separately deployed operations UI: JSON only, bearer sessions,
/// permissions enforced here for every request. 401 without a valid session, 403 for a role
/// that may not, 409 for a version the object has moved past, 422 for a rule the operator
/// broke. Refusals by role and by rule are audited; a lost race is not a refusal.
/// </summary>
public static class AdminEndpoints
{
    public const string Prefix = "/api/admin/v1";
    static readonly JsonSerializerOptions Json = ChatEndpoints.Json;

    public static void MapAdminEndpoints(this IEndpointRouteBuilder app, AdminServices s, AdminConfig cfg, ILogger logger)
    {
        var g = app.MapGroup(Prefix);

        // ---- session ---------------------------------------------------------------------
        g.MapPost("/session", async (HttpContext http, CancellationToken ct) =>
        {
            var body = await Read<LoginRequest>(http, ct);
            if (body is null || string.IsNullOrWhiteSpace(body.Username) || string.IsNullOrEmpty(body.Password))
                return Problem(400, "Malformed request", "username and password are required");
            var session = await s.Sessions.LoginAsync(body.Username.Trim(), body.Password, ct);
            if (session is null)
            {
                logger.LogWarning("failed staff login for {Username}", body.Username.Trim());
                return Problem(401, "Sign-in failed", "The username or password is wrong, or the account is disabled.");
            }
            return Results.Json(new { token = session.Token, username = session.Actor.Username, role = session.Actor.Role.Name(), expiresAt = session.ExpiresAt }, Json);
        });

        g.MapDelete("/session", async (HttpContext http, CancellationToken ct) =>
        {
            if (Token(http) is { } token) await s.Sessions.LogoutAsync(token, ct);
            return Results.NoContent();
        });

        // ---- everything below needs a signed-in operator ----------------------------------
        var auth = g.MapGroup("").AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            var token = Token(http);
            var actor = token is null ? null : await s.Sessions.ResolveAsync(token, http.RequestAborted);
            if (actor is null) return Problem(401, "Not signed in", "A valid staff session is required.");
            http.Items["actor"] = actor;
            return await next(ctx);
        });

        auth.MapGet("/me", (HttpContext http) => { var a = ActorOf(http); return Results.Json(new { username = a.Username, role = a.Role.Name() }, Json); });

        auth.MapGet("/overview", async (HttpContext http, CancellationToken ct) =>
            Results.Json(await s.Conversations.OverviewAsync(TimeSpan.FromDays(7), ct), Json));

        // ---- staff (admin only) -------------------------------------------------------------
        auth.MapGet("/staff", async (HttpContext http, CancellationToken ct) =>
            await AdminOnly(http, s, "list staff", null, ct) ?? Results.Json((await s.Accounts.ListAsync(ct)).Select(View), Json));

        auth.MapPost("/staff", async (HttpContext http, CancellationToken ct) =>
        {
            if (await AdminOnly(http, s, "create staff", null, ct) is { } refused) return refused;
            var body = await Read<NewStaff>(http, ct);
            if (body is null || !StaffRoles.TryParse(body.Role, out var role))
                return Problem(400, "Malformed request", "username, password and a role of admin or support are required");
            try
            {
                await s.Accounts.CreateAsync(body.Username?.Trim() ?? "", body.Password ?? "", role, ct);
            }
            catch (ArgumentException ex) { return Problem(422, "Cannot create account", ex.Message); }
            catch (DuplicateStaffAccountException ex) { return Problem(409, "Account exists", ex.Message); }
            await s.Audit.RecordAsync(ActorOf(http).Username, "create staff", "staff", body.Username!.Trim(), "ok", $"role {role.Name()}", ct);
            return Results.Json(View((await s.Accounts.ListAsync(ct)).First(a => a.Username == body.Username!.Trim())), Json, statusCode: 201);
        });

        auth.MapPatch("/staff/{username}", async (string username, HttpContext http, CancellationToken ct) =>
        {
            if (await AdminOnly(http, s, "update staff", username, ct) is { } refused) return refused;
            var body = await Read<StaffPatch>(http, ct);
            if (body is null) return Problem(400, "Malformed request", "a JSON body is required");
            StaffRole? role = null;
            if (body.Role is not null) { if (!StaffRoles.TryParse(body.Role, out var r)) return Problem(400, "Malformed request", "role must be admin or support"); role = r; }
            var me = ActorOf(http);
            if (username == me.Username && (body.Enabled == false || role == StaffRole.Support))
            {
                await s.Audit.RecordAsync(me.Username, "update staff", "staff", username, "refused", "an admin cannot disable or demote their own account", ct);
                return Problem(422, "Not allowed", "An admin cannot disable or demote their own account.");
            }
            try
            {
                if (!await s.Accounts.UpdateAsync(username, role, body.Enabled, body.Password, ct)) return Problem(404, "No such account", null);
            }
            catch (ArgumentException ex) { return Problem(422, "Cannot update account", ex.Message); }
            var changes = string.Join(", ", new[] { role is null ? null : $"role {role.Value.Name()}", body.Enabled is null ? null : (body.Enabled.Value ? "enabled" : "disabled"), body.Password is null ? null : "password reset" }.Where(x => x is not null));
            await s.Audit.RecordAsync(me.Username, "update staff", "staff", username, "ok", changes, ct);
            return Results.Json(View((await s.Accounts.ListAsync(ct)).First(a => a.Username == username)), Json);
        });

        // ---- tickets ----------------------------------------------------------------------
        auth.MapGet("/tickets", async (HttpContext http, string? state, string? owner, string? conversationId, DateTimeOffset? from, DateTimeOffset? to, int? page, int? size, CancellationToken ct) =>
        {
            TicketState? st = null;
            if (!string.IsNullOrEmpty(state)) { if (!TicketStates.TryParse(state, out var parsed)) return Problem(400, "Malformed request", "unknown state"); st = parsed; }
            var (p, n) = Paging.Bound(page, size);
            return Results.Json(await s.Tickets.SearchAsync(new TicketFilter(st, Blank(owner), Blank(conversationId), from, to), p, n, ct), Json);
        });

        auth.MapGet("/tickets/{number}", async (string number, CancellationToken ct) =>
            await s.Tickets.GetAsync(number, ct) is { } d ? Results.Json(d, Json) : Problem(404, "No such ticket", null));

        foreach (var action in new[] { "claim", "assign", "release", "resolve", "close", "reopen", "note" })
        {
            auth.MapPost($"/tickets/{{number}}/{action}", async (string number, HttpContext http, CancellationToken ct) =>
            {
                var actor = ActorOf(http);
                var body = await Read<TicketCommand>(http, ct);
                // The version is required, never defaulted: two operators with the same ticket
                // open otherwise overwrite each other and the loser is told nothing.
                if (body?.ExpectedVersion is null) return Problem(400, "Malformed request", "expectedVersion is required");
                var v = body.ExpectedVersion.Value;
                try
                {
                    var result = action switch
                    {
                        "claim" => await s.Tickets.ClaimAsync(number, v, actor, ct),
                        "assign" => await s.Tickets.AssignAsync(number, v, body.Assignee ?? "", actor, ct),
                        "release" => await s.Tickets.ReleaseAsync(number, v, actor, ct),
                        "resolve" => await s.Tickets.ResolveAsync(number, v, body.Text, actor, ct),
                        "close" => await s.Tickets.CloseAsync(number, v, body.Text, actor, ct),
                        "reopen" => await s.Tickets.ReopenAsync(number, v, body.Text, actor, ct),
                        _ => await s.Tickets.NoteAsync(number, v, body.Text, actor, ct),
                    };
                    return Results.Json(result, Json);
                }
                catch (TicketNotFoundException) { return Problem(404, "No such ticket", null); }
                catch (TicketConflictException ex) { return Problem(409, "Ticket has changed", ex.Message + ". Refresh and retry."); }
                catch (TicketRuleException ex)
                {
                    await s.Audit.RecordAsync(actor.Username, $"ticket {action}", "ticket", number, "refused", ex.Message, ct);
                    return Problem(422, "Not allowed", ex.Message);
                }
            });
        }

        // ---- conversations ----------------------------------------------------------------
        auth.MapGet("/conversations", async (string? q, string? outcome, DateTimeOffset? from, DateTimeOffset? to, int? page, int? size, CancellationToken ct) =>
        {
            var (p, n) = Paging.Bound(page, size);
            return Results.Json(await s.Conversations.SearchAsync(new ConversationFilter(Blank(q), Blank(outcome), from, to), p, n, ct), Json);
        });

        auth.MapGet("/conversations/{id}", async (string id, HttpContext http, CancellationToken ct) =>
        {
            var detail = await s.Conversations.GetAsync(id, ct);
            if (detail is null) return Problem(404, "No such conversation", null);
            // Reading is an action. Who looked is most of what an audit trail is for here.
            await s.Audit.RecordAsync(ActorOf(http).Username, "read conversation", "conversation", id, "ok", null, ct);
            return Results.Json(detail, Json);
        });

        // ---- feedback -----------------------------------------------------------------------
        auth.MapPost("/conversations/{id}/turns/{turnId:guid}/feedback", async (string id, Guid turnId, HttpContext http, CancellationToken ct) =>
        {
            var actor = ActorOf(http);
            var body = await Read<NewFeedback>(http, ct);
            if (body is null || string.IsNullOrWhiteSpace(body.Issue)) return Problem(400, "Malformed request", "issue is required");
            if (!await s.Conversations.TurnExistsAsync(turnId, id, ct)) return Problem(404, "No such turn", null);
            try
            {
                var created = await s.Feedback.CreateAsync(turnId, id, body.Issue.Trim().ToLowerInvariant(), body.Note, actor.Username, ct);
                await s.Audit.RecordAsync(actor.Username, "flag answer", "turn", turnId.ToString(), "ok", created.Issue, ct);
                return Results.Json(created, Json, statusCode: 201);
            }
            catch (FeedbackRuleException ex) { return Problem(422, "Not allowed", ex.Message); }
        });

        auth.MapGet("/feedback", async (string? state, int? page, int? size, CancellationToken ct) =>
        {
            var (p, n) = Paging.Bound(page, size);
            return Results.Json(await s.Feedback.SearchAsync(Blank(state), p, n, ct), Json);
        });

        auth.MapPost("/feedback/{id:long}/close", async (long id, HttpContext http, CancellationToken ct) =>
        {
            var actor = ActorOf(http);
            var body = await Read<CloseFeedback>(http, ct);
            if (body?.ExpectedVersion is null) return Problem(400, "Malformed request", "expectedVersion is required");
            try
            {
                var closed = await s.Feedback.CloseAsync(id, body.ExpectedVersion.Value, body.Conclusion, actor.Username, ct);
                await s.Audit.RecordAsync(actor.Username, "close feedback", "feedback", id.ToString(), "ok", null, ct);
                return Results.Json(closed, Json);
            }
            catch (FeedbackConflictException ex) { return Problem(409, "Feedback has changed", ex.Message); }
            catch (FeedbackRuleException ex)
            {
                await s.Audit.RecordAsync(actor.Username, "close feedback", "feedback", id.ToString(), "refused", ex.Message, ct);
                return Problem(422, "Not allowed", ex.Message);
            }
        });

        // ---- audit (admin only) -------------------------------------------------------------
        auth.MapGet("/audit-events", async (HttpContext http, string? actor, string? action, int? page, int? size, CancellationToken ct) =>
        {
            if (await AdminOnly(http, s, "read audit", null, ct) is { } refused) return refused;
            var (p, n) = Paging.Bound(page, size);
            return Results.Json(await s.Audit.SearchAsync(Blank(actor), Blank(action), p, n, ct), Json);
        });
    }

    // ---- shapes --------------------------------------------------------------------------------
    public sealed record LoginRequest(string? Username, string? Password);
    public sealed record NewStaff(string? Username, string? Password, string? Role);
    public sealed record StaffPatch(string? Role, bool? Enabled, string? Password);
    public sealed record TicketCommand(int? ExpectedVersion, string? Assignee, string? Text);
    public sealed record NewFeedback(string? Issue, string? Note);
    public sealed record CloseFeedback(int? ExpectedVersion, string? Conclusion);

    static object View(StaffAccount a) => new { username = a.Username, role = a.Role.Name(), enabled = a.Enabled, createdAt = a.CreatedAt };

    // ---- plumbing ------------------------------------------------------------------------------
    static string? Token(HttpContext http)
    {
        var h = http.Request.Headers.Authorization.ToString();
        return h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && h.Length > 7 ? h[7..].Trim() : null;
    }

    static Actor ActorOf(HttpContext http) => (Actor)http.Items["actor"]!;

    /// <summary>Null when the actor is an admin; otherwise the 403, already audited.</summary>
    static async Task<IResult?> AdminOnly(HttpContext http, AdminServices s, string action, string? objectId, CancellationToken ct)
    {
        var actor = ActorOf(http);
        if (actor.IsAdmin) return null;
        await s.Audit.RecordAsync(actor.Username, action, null, objectId, "refused", $"role {actor.Role.Name()} may not {http.Request.Method} {http.Request.Path}", ct);
        return Problem(403, "Not permitted", "This action needs the admin role.");
    }

    static async Task<T?> Read<T>(HttpContext http, CancellationToken ct) where T : class
    {
        try { return await JsonSerializer.DeserializeAsync<T>(http.Request.Body, Json, ct); }
        catch (JsonException) { return null; }
    }

    static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    static IResult Problem(int status, string title, string? detail) =>
        Results.Json(new Problem("about:blank", title, status, detail), Json, "application/problem+json", status);
}
