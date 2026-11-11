using EPTradeBot.Api.Security;
using EPTradeBot.Api.Validation;
using EPTradeBot.Core.Contracts.Management;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Infrastructure.Persistence;
using EPTradeBot.Infrastructure.Scheduling;
using Microsoft.EntityFrameworkCore;

namespace EPTradeBot.Api.Endpoints;

/// <summary>Hosted strategies and their India-time schedules. Admin-token calls.</summary>
internal static class HostedStrategyEndpoints
{
    public static RouteGroupBuilder MapHostedStrategies(this RouteGroupBuilder v1)
    {
        var admin = v1.MapGroup("/hosted-strategies").AddEndpointFilter<AdminTokenFilter>().WithDataAnnotations();

        admin.MapGet("", async (EpTradeDbContext db, CancellationToken ct) =>
            Results.Ok((await db.HostedStrategies.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct)).Select(View)));

        admin.MapGet("/{id:guid}", async (Guid id, EpTradeDbContext db, CancellationToken ct) =>
            await db.HostedStrategies.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, ct) is { } s ? Results.Ok(View(s)) : Missing());

        admin.MapPost("", async (CreateHostedStrategyRequest request, EpTradeDbContext db, ScheduleRegistry registry, CancellationToken ct) =>
        {
            if (Parse(request, out var start, out var stop, out var days) is { } problem) return problem;
            if (request.AccountId is { } account && !await db.BrokerAccounts.AnyAsync(a => a.Id == account, ct))
                return Results.Problem(title: "Not found.", detail: "This account does not exist.", statusCode: StatusCodes.Status404NotFound);
            if (await db.HostedStrategies.AnyAsync(s => s.Name == request.Name, ct))
                return Results.Problem(title: "Already exists.", detail: "A strategy with this name already exists.", statusCode: StatusCodes.Status409Conflict);

            var s = new HostedStrategy
            {
                Name = request.Name,
                ScriptPath = request.ScriptPath,
                AccountId = request.AccountId,
                ExecutionMode = request.ExecutionMode == "auto" ? ExecutionMode.Auto : ExecutionMode.SemiAuto,
                Exchange = (request.Exchange ?? "NSE").ToUpperInvariant(),
                IsEnabled = request.IsEnabled ?? true,
                IsScheduled = request.IsScheduled ?? false,
                ScheduleStartIst = start,
                ScheduleStopIst = stop,
                ScheduleDays = days,
            };
            db.HostedStrategies.Add(s);
            await db.SaveChangesAsync(ct);
            await registry.SyncAsync(s.Id, ct);
            return Results.Created($"/api/v1/hosted-strategies/{s.Id}", View(s));
        });

        admin.MapPut("/{id:guid}/schedule", async (Guid id, UpdateScheduleRequest request, EpTradeDbContext db, ScheduleRegistry registry, CancellationToken ct) =>
        {
            var s = await db.HostedStrategies.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (s is null) return Missing();
            if (Parse(request, out var start, out var stop, out var days) is { } problem) return problem;
            if (request.Exchange is not null) s.Exchange = request.Exchange.ToUpperInvariant();
            if (request.IsEnabled is { } enabled) s.IsEnabled = enabled;
            if (request.IsScheduled is { } scheduled) s.IsScheduled = scheduled;
            s.ScheduleStartIst = start;
            s.ScheduleStopIst = stop;
            s.ScheduleDays = days;
            s.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await registry.SyncAsync(s.Id, ct);
            return Results.Ok(View(s));
        });

        admin.MapDelete("/{id:guid}", async (Guid id, EpTradeDbContext db, ScheduleRegistry registry, CancellationToken ct) =>
        {
            var s = await db.HostedStrategies.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (s is null) return Missing();
            if (s.IsRunning)
                return Results.Problem(title: "Still running.", detail: "Stop the strategy before deleting it.", statusCode: StatusCodes.Status409Conflict);
            db.HostedStrategies.Remove(s);
            await db.SaveChangesAsync(ct);
            await registry.SyncAsync(id, ct);
            return Results.NoContent();
        });

        admin.MapPost("/{id:guid}/start", async (Guid id, StrategyScheduleService service, EpTradeDbContext db, CancellationToken ct) =>
        {
            if (!await db.HostedStrategies.AnyAsync(s => s.Id == id, ct)) return Missing();
            var result = await service.StartManuallyAsync(id, ct);
            return result.Ok
                ? Results.Ok(new { status = "success", message = result.Message })
                : Results.Problem(title: "Not started.", detail: result.Message, statusCode: StatusCodes.Status409Conflict);
        });

        admin.MapPost("/{id:guid}/stop", async (Guid id, StrategyScheduleService service, EpTradeDbContext db, CancellationToken ct) =>
        {
            if (!await db.HostedStrategies.AnyAsync(s => s.Id == id, ct)) return Missing();
            var result = await service.StopManuallyAsync(id, ct);
            return result.Ok
                ? Results.Ok(new { status = "success", message = result.Message })
                : Results.Problem(title: "Not stopped.", detail: result.Message, statusCode: StatusCodes.Status409Conflict);
        });

        return v1;
    }

    private static IResult Missing() => Results.Problem(title: "Not found.", detail: "This strategy does not exist.", statusCode: StatusCodes.Status404NotFound);

    private static IResult? Parse(IScheduleSettings request, out TimeOnly? start, out TimeOnly? stop, out TradingDays days)
    {
        start = request.ScheduleStart is { } a ? TimeOnly.ParseExact(a, "HH:mm") : null;
        stop = request.ScheduleStop is { } b ? TimeOnly.ParseExact(b, "HH:mm") : null;
        days = TradingDays.Weekdays;
        if (request.ScheduleDays is { } names)
        {
            days = TradingDays.None;
            foreach (var raw in names)
            {
                var index = ScheduleFormat.DayNames.ToList().IndexOf(raw.Trim().ToLowerInvariant()[..Math.Min(3, raw.Trim().Length)]);
                if (index < 0)
                    return Results.Problem(title: "Invalid days.", detail: $"'{raw}' is not a day. Use mon, tue, wed, thu, fri, sat or sun.", statusCode: StatusCodes.Status400BadRequest);
                days |= (TradingDays)(1 << index);
            }
        }

        return null;
    }

    private static object View(HostedStrategy s) => new
    {
        id = s.Id,
        name = s.Name,
        script_path = s.ScriptPath,
        account_id = s.AccountId,
        execution_mode = s.ExecutionMode == ExecutionMode.Auto ? "auto" : "semi_auto",
        exchange = s.Exchange,
        is_enabled = s.IsEnabled,
        is_scheduled = s.IsScheduled,
        schedule_start = s.ScheduleStartIst?.ToString("HH:mm"),
        schedule_stop = s.ScheduleStopIst?.ToString("HH:mm"),
        schedule_days = ScheduleFormat.DayNames.Where((_, i) => ((int)s.ScheduleDays & (1 << i)) != 0).ToList(),
        is_running = s.IsRunning,
        manually_stopped = s.ManuallyStopped,
        paused_reason = s.PausedReason,
        last_started_at_utc = s.LastStartedAtUtc,
        last_stopped_at_utc = s.LastStoppedAtUtc,
        last_error = s.LastError,
    };
}
