using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Scheduling;
using EPTradeBot.Infrastructure.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Quartz;

namespace EPTradeBot.IntegrationTests;

public sealed class ScheduleTests
{
    private sealed class Clock(DateTimeOffset at) : TimeProvider
    {
        public DateTimeOffset At { get; set; } = at;

        public override DateTimeOffset GetUtcNow() => At;
    }

    private sealed class Recorder : IStrategyController
    {
        public List<string> Calls { get; } = [];

        public HashSet<Guid> Failing { get; } = [];

        public HashSet<Guid> Throwing { get; } = [];

        public Task<ControllerResult> StartAsync(Guid id, CancellationToken ct)
        {
            Calls.Add($"start:{id:N}");
            if (Throwing.Contains(id)) throw new InvalidOperationException("boom");
            return Task.FromResult(Failing.Contains(id) ? new ControllerResult(false, "The script would not start.") : new ControllerResult(true, "ok"));
        }

        public Task<ControllerResult> StopAsync(Guid id, string reason, CancellationToken ct)
        {
            Calls.Add($"stop:{id:N}:{reason}");
            if (Throwing.Contains(id)) throw new InvalidOperationException("boom");
            return Task.FromResult(Failing.Contains(id) ? new ControllerResult(false, "It would not stop.") : new ControllerResult(true, "ok"));
        }
    }

    private sealed record Setup(TestHost Host, Clock Clock, Recorder Controller);

    // 2026-11-11 is a Wednesday and a trading day, the 10th a seeded holiday, the 14th a Saturday, the 16th a Monday.
    private static DateTimeOffset Ist(int day, int hour, int minute) => new(2026, 11, day, hour, minute, 0, TimeSpan.FromHours(5.5));

    // Trigger arithmetic is checked in a week that is always in the future, because Quartz starts a trigger at the real clock:
    // January 2030, where the 2nd is a Wednesday, the 5th a Saturday and the 7th a Monday.
    private static DateTimeOffset Fut(int day, int hour, int minute) => new(2030, 1, day, hour, minute, 0, TimeSpan.FromHours(5.5));

    private static Setup New(DateTimeOffset at)
    {
        var clock = new Clock(at);
        var controller = new Recorder();
        var host = new TestHost("Development", b => b.ConfigureServices(s =>
        {
            s.AddSingleton<TimeProvider>(clock);
            s.AddSingleton<IStrategyController>(controller);
        }));
        return new Setup(host, clock, controller);
    }

    private static async Task<Guid> CreateAsync(TestHost host, string name = "alpha", object? extra = null)
    {
        var body = new JsonObject
        {
            ["name"] = name, ["script_path"] = $"{name}.py", ["is_scheduled"] = true, ["exchange"] = "NSE",
            ["schedule_start"] = "09:15", ["schedule_stop"] = "15:25", ["schedule_days"] = new JsonArray("mon", "tue", "wed", "thu", "fri"),
        };
        if (extra is JsonObject over) foreach (var (k, v) in over.ToList()) { over.Remove(k); body[k] = v; }
        var response = await host.Admin.PostAsJsonAsync("/api/v1/hosted-strategies", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Guid.Parse((await response.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<string>());
    }

    private static async Task<HostedStrategy> RowAsync(TestHost host, Guid id)
    {
        await using var db = host.NewDb();
        return await db.HostedStrategies.AsNoTracking().SingleAsync(s => s.Id == id);
    }

    private static async Task<DateTimeOffset?> NextFireAsync(TestHost host, JobKey key, DateTimeOffset after)
    {
        var scheduler = await host.Get<ISchedulerFactory>().GetScheduler();
        var trigger = await scheduler.GetTrigger(new TriggerKey(key.Name, key.Group));
        return trigger?.GetFireTimeAfter(after);
    }

    [PostgresFact]
    public async Task A_schedule_fires_at_its_india_time_on_its_days_only()
    {
        var s = New(Ist(11, 8, 0));
        await using var _ = s.Host;
        var id = await CreateAsync(s.Host);

        // After Friday afternoon the next start is Monday 09:15 IST, which is 03:45 UTC.
        var next = await NextFireAsync(s.Host, ScheduleRegistry.StartJobKey(id), Fut(4, 16, 0));
        Assert.Equal(Fut(7, 9, 15).ToUniversalTime(), next!.Value.ToUniversalTime());
        Assert.Equal(new DateTime(2030, 1, 7, 3, 45, 0, DateTimeKind.Utc), next.Value.UtcDateTime);

        // The stop is the same days at 15:25.
        Assert.Equal(Fut(3, 15, 25).ToUniversalTime(), (await NextFireAsync(s.Host, ScheduleRegistry.StopJobKey(id), Fut(2, 15, 25)))!.Value.ToUniversalTime());
    }

    [PostgresFact]
    public async Task Weekend_days_chosen_by_the_trader_get_triggers_too()
    {
        var s = New(Ist(11, 8, 0));
        await using var _ = s.Host;
        var id = await CreateAsync(s.Host, extra: new JsonObject { ["schedule_days"] = new JsonArray("sat") });
        var next = await NextFireAsync(s.Host, ScheduleRegistry.StartJobKey(id), Fut(2, 8, 0));
        Assert.Equal(Fut(5, 9, 15).ToUniversalTime(), next!.Value.ToUniversalTime());
    }

    [PostgresFact]
    public async Task Changing_disabling_or_deleting_a_strategy_changes_its_triggers()
    {
        var s = New(Ist(11, 8, 0));
        await using var _ = s.Host;
        var id = await CreateAsync(s.Host);

        var changed = await s.Host.Admin.PutAsJsonAsync($"/api/v1/hosted-strategies/{id}/schedule",
            new { schedule_start = "10:00", schedule_stop = "14:00", schedule_days = new[] { "mon" } });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal(Fut(7, 10, 0).ToUniversalTime(), (await NextFireAsync(s.Host, ScheduleRegistry.StartJobKey(id), Fut(2, 8, 0)))!.Value.ToUniversalTime());

        var off = await s.Host.Admin.PutAsJsonAsync($"/api/v1/hosted-strategies/{id}/schedule", new { is_scheduled = false, schedule_start = "10:00" });
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.Null(await NextFireAsync(s.Host, ScheduleRegistry.StartJobKey(id), Ist(11, 8, 0)));
        Assert.Null(await NextFireAsync(s.Host, ScheduleRegistry.StopJobKey(id), Ist(11, 8, 0)));

        await s.Host.Admin.PutAsJsonAsync($"/api/v1/hosted-strategies/{id}/schedule", new { is_scheduled = true, schedule_start = "10:00" });
        Assert.NotNull(await NextFireAsync(s.Host, ScheduleRegistry.StartJobKey(id), Ist(11, 8, 0)));
        Assert.Null(await NextFireAsync(s.Host, ScheduleRegistry.StopJobKey(id), Ist(11, 8, 0)));

        Assert.Equal(HttpStatusCode.NoContent, (await s.Host.Admin.DeleteAsync($"/api/v1/hosted-strategies/{id}")).StatusCode);
        Assert.Null(await NextFireAsync(s.Host, ScheduleRegistry.StartJobKey(id), Ist(11, 8, 0)));
    }

    [PostgresFact]
    public async Task A_restart_rebuilds_every_trigger_from_the_database_and_adds_the_two_system_jobs()
    {
        var s = New(Ist(11, 8, 0));
        await using var _ = s.Host;
        var id = await CreateAsync(s.Host);
        var scheduler = await s.Host.Get<ISchedulerFactory>().GetScheduler();
        await scheduler.Clear();
        Assert.Null(await NextFireAsync(s.Host, ScheduleRegistry.StartJobKey(id), Ist(11, 8, 0)));

        await s.Host.Get<ScheduleRegistry>().StartAsync(CancellationToken.None);

        Assert.Equal(Fut(2, 9, 15).ToUniversalTime(), (await NextFireAsync(s.Host, ScheduleRegistry.StartJobKey(id), Fut(2, 8, 0)))!.Value.ToUniversalTime());
        // The trading-day check is 00:01 IST, which is 18:31 UTC the evening before; the enforcer runs every minute.
        Assert.Equal(Fut(3, 0, 1).ToUniversalTime(), (await NextFireAsync(s.Host, ScheduleRegistry.DailyCheckKey, Fut(2, 8, 0)))!.Value.ToUniversalTime());
        Assert.Equal(Fut(2, 8, 1).ToUniversalTime(), (await NextFireAsync(s.Host, ScheduleRegistry.EnforcerKey, Fut(2, 8, 0)))!.Value.ToUniversalTime());
    }

    [PostgresFact]
    public async Task A_scheduled_start_on_a_trading_day_starts_the_strategy_once()
    {
        var s = New(Ist(11, 9, 15));
        await using var _ = s.Host;
        var id = await CreateAsync(s.Host);
        var service = s.Host.Get<StrategyScheduleService>();

        Assert.Equal("started", await service.RunScheduledStartAsync(id, default));
        Assert.Equal("already_running", await service.RunScheduledStartAsync(id, default));

        Assert.Equal([$"start:{id:N}"], s.Controller.Calls);
        var row = await RowAsync(s.Host, id);
        Assert.True(row.IsRunning);
        Assert.Equal(s.Clock.At.UtcDateTime, row.LastStartedAtUtc);
    }

    [PostgresFact]
    public async Task A_scheduled_start_on_a_weekend_or_a_holiday_is_refused_and_the_strategy_is_paused()
    {
        var s = New(Ist(14, 9, 15));
        await using var _ = s.Host;
        var id = await CreateAsync(s.Host, extra: new JsonObject { ["schedule_days"] = new JsonArray("mon", "tue", "wed", "thu", "fri", "sat") });
        var service = s.Host.Get<StrategyScheduleService>();

        Assert.Equal("weekend", await service.RunScheduledStartAsync(id, default));
        var row = await RowAsync(s.Host, id);
        Assert.False(row.IsRunning);
        Assert.Equal("weekend", row.PausedReason);
        Assert.Contains("weekend", row.LastError);

        s.Clock.At = Ist(10, 9, 15); // the seeded Balipratipada holiday
        Assert.Equal("holiday", await service.RunScheduledStartAsync(id, default));
        Assert.Equal("holiday", (await RowAsync(s.Host, id)).PausedReason);
        Assert.Empty(s.Controller.Calls);
    }

    [PostgresFact]
    public async Task A_seeded_special_session_on_a_sunday_is_a_trading_day()
    {
        var s = New(Ist(8, 17, 45)); // Diwali Muhurat, a Sunday
        await using var _ = s.Host;
        var id = await CreateAsync(s.Host, extra: new JsonObject { ["schedule_days"] = new JsonArray("sun"), ["schedule_start"] = "17:45", ["schedule_stop"] = "19:00" });
        Assert.Equal("started", await s.Host.Get<StrategyScheduleService>().RunScheduledStartAsync(id, default));
    }

    [PostgresFact]
    public async Task A_day_the_trader_did_not_choose_is_skipped_without_a_pause()
    {
        var s = New(Ist(11, 9, 15));
        await using var _ = s.Host;
        var id = await CreateAsync(s.Host, extra: new JsonObject { ["schedule_days"] = new JsonArray("mon") });
        Assert.Equal(ScheduleRules.NotToday, await s.Host.Get<StrategyScheduleService>().RunScheduledStartAsync(id, default));
        Assert.Null((await RowAsync(s.Host, id)).PausedReason);
        Assert.Empty(s.Controller.Calls);
    }

    [PostgresFact]
    public async Task A_strategy_that_would_not_start_is_not_recorded_as_running()
    {
        var s = New(Ist(11, 9, 15));
        await using var _ = s.Host;
        var id = await CreateAsync(s.Host);
        s.Controller.Failing.Add(id);
        Assert.Equal("start_failed", await s.Host.Get<StrategyScheduleService>().RunScheduledStartAsync(id, default));
        var row = await RowAsync(s.Host, id);
        Assert.False(row.IsRunning);
        Assert.Equal("The script would not start.", row.LastError);
    }

    [PostgresFact]
    public async Task A_disabled_strategy_is_not_started_by_a_stale_trigger()
    {
        var s = New(Ist(11, 9, 15));
        await using var _ = s.Host;
        var id = await CreateAsync(s.Host, extra: new JsonObject { ["is_enabled"] = false });
        Assert.Equal("skipped", await s.Host.Get<StrategyScheduleService>().RunScheduledStartAsync(id, default));
        Assert.Empty(s.Controller.Calls);
    }

    [PostgresFact]
    public async Task A_scheduled_stop_stops_a_running_strategy_and_ignores_one_that_is_not()
    {
        var s = New(Ist(11, 9, 15));
        await using var _ = s.Host;
        var id = await CreateAsync(s.Host);
        var service = s.Host.Get<StrategyScheduleService>();
        await using (var db = s.Host.NewDb())
            await db.HostedStrategies.Where(x => x.Id == id).ExecuteUpdateAsync(u => u.SetProperty(x => x.PausedReason, "weekend"));
        Assert.Equal("not_running", await service.RunScheduledStopAsync(id, default));
        Assert.Empty(s.Controller.Calls);
        // The stop of the day ends any pause, so the next start is judged afresh.
        Assert.Null((await RowAsync(s.Host, id)).PausedReason);

        await service.RunScheduledStartAsync(id, default);
        s.Clock.At = Ist(11, 15, 25);
        Assert.Equal("stopped", await service.RunScheduledStopAsync(id, default));
        var row = await RowAsync(s.Host, id);
        Assert.False(row.IsRunning);
        Assert.Equal(s.Clock.At.UtcDateTime, row.LastStoppedAtUtc);
        Assert.Contains($"stop:{id:N}:scheduled_stop", s.Controller.Calls);
    }

    [PostgresFact]
    public async Task A_stop_by_hand_holds_until_the_trader_starts_it_again()
    {
        var s = New(Ist(11, 9, 15));
        await using var _ = s.Host;
        var id = await CreateAsync(s.Host);
        var service = s.Host.Get<StrategyScheduleService>();
        await service.RunScheduledStartAsync(id, default);

        Assert.Equal(HttpStatusCode.OK, (await s.Host.Admin.PostAsync($"/api/v1/hosted-strategies/{id}/stop", null)).StatusCode);
        Assert.True((await RowAsync(s.Host, id)).ManuallyStopped);

        s.Clock.At = Ist(12, 9, 15);
        Assert.Equal(ScheduleRules.ManualStop, await service.RunScheduledStartAsync(id, default));
        Assert.False((await RowAsync(s.Host, id)).IsRunning);

        Assert.Equal(HttpStatusCode.OK, (await s.Host.Admin.PostAsync($"/api/v1/hosted-strategies/{id}/start", null)).StatusCode);
        var row = await RowAsync(s.Host, id);
        Assert.True(row.IsRunning);
        Assert.False(row.ManuallyStopped);
    }

    [PostgresFact]
    public async Task The_enforcer_stops_on_a_closed_day_and_resumes_when_the_exchange_trades_again()
    {
        var s = New(Ist(14, 10, 0)); // Saturday
        await using var _ = s.Host;
        var id = await CreateAsync(s.Host);
        await using (var db = s.Host.NewDb())
            await db.HostedStrategies.Where(x => x.Id == id).ExecuteUpdateAsync(u => u.SetProperty(x => x.IsRunning, true));
        var service = s.Host.Get<StrategyScheduleService>();

        Assert.Equal(1, await service.RunEnforcerAsync(default));
        var row = await RowAsync(s.Host, id);
        Assert.False(row.IsRunning);
        Assert.Equal("weekend", row.PausedReason);
        Assert.Contains($"stop:{id:N}:weekend", s.Controller.Calls);

        // Nothing more to do the next minute, and nothing while the exchange is still shut.
        Assert.Equal(0, await service.RunEnforcerAsync(default));

        s.Clock.At = Ist(16, 9, 10); // Monday, before the trader's 09:15 start
        Assert.Equal(0, await service.RunEnforcerAsync(default));
        s.Clock.At = Ist(16, 9, 20);
        Assert.Equal(1, await service.RunEnforcerAsync(default));
        row = await RowAsync(s.Host, id);
        Assert.True(row.IsRunning);
        Assert.Null(row.PausedReason);
    }

    [PostgresFact]
    public async Task A_stop_that_failed_leaves_the_strategy_running_and_says_why()
    {
        var s = New(Ist(14, 10, 0)); // Saturday
        await using var _ = s.Host;
        var id = await CreateAsync(s.Host);
        await using (var db = s.Host.NewDb())
            await db.HostedStrategies.Where(x => x.Id == id).ExecuteUpdateAsync(u => u.SetProperty(x => x.IsRunning, true));
        s.Controller.Failing.Add(id);

        await s.Host.Get<StrategyScheduleService>().RunEnforcerAsync(default);

        var row = await RowAsync(s.Host, id);
        Assert.True(row.IsRunning);
        Assert.Null(row.PausedReason);
        Assert.Equal("It would not stop.", row.LastError);
    }

    [PostgresFact]
    public async Task One_strategy_failing_does_not_stop_the_others_being_checked()
    {
        var s = New(Ist(14, 10, 0));
        await using var _ = s.Host;
        var bad = await CreateAsync(s.Host, "bad");
        var good = await CreateAsync(s.Host, "good");
        await using (var db = s.Host.NewDb())
            await db.HostedStrategies.ExecuteUpdateAsync(u => u.SetProperty(x => x.IsRunning, true));
        s.Controller.Throwing.Add(bad);

        Assert.Equal(1, await s.Host.Get<StrategyScheduleService>().RunEnforcerAsync(default));
        Assert.False((await RowAsync(s.Host, good)).IsRunning);
        Assert.True((await RowAsync(s.Host, bad)).IsRunning);
    }

    [PostgresFact]
    public async Task The_daily_check_stops_a_running_strategy_on_a_holiday_but_not_a_crypto_one()
    {
        var s = New(Ist(10, 0, 1));
        await using var _ = s.Host;
        var equity = await CreateAsync(s.Host, "equity");
        var crypto = await CreateAsync(s.Host, "crypto", new JsonObject { ["exchange"] = "CRYPTO", ["schedule_start"] = "00:00" });
        await using (var db = s.Host.NewDb())
            await db.HostedStrategies.ExecuteUpdateAsync(u => u.SetProperty(x => x.IsRunning, true));

        Assert.Equal(1, await s.Host.Get<StrategyScheduleService>().RunDailyCheckAsync(default));
        Assert.False((await RowAsync(s.Host, equity)).IsRunning);
        Assert.Equal("holiday", (await RowAsync(s.Host, equity)).PausedReason);
        Assert.True((await RowAsync(s.Host, crypto)).IsRunning);
    }

    [PostgresFact]
    public async Task A_schedule_that_cannot_be_read_is_refused_in_words_a_trader_can_use()
    {
        var s = New(Ist(11, 9, 15));
        await using var _ = s.Host;
        var id = await CreateAsync(s.Host);

        var badTime = await s.Host.Admin.PutAsJsonAsync($"/api/v1/hosted-strategies/{id}/schedule", new { schedule_start = "9:15am" });
        Assert.Equal(HttpStatusCode.BadRequest, badTime.StatusCode);
        Assert.Contains("HH:mm", await badTime.Content.ReadAsStringAsync());

        var badDay = await s.Host.Admin.PutAsJsonAsync($"/api/v1/hosted-strategies/{id}/schedule", new { schedule_days = new[] { "funday" } });
        Assert.Equal(HttpStatusCode.BadRequest, badDay.StatusCode);
        Assert.Contains("funday", await badDay.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.Conflict, (await s.Host.Admin.PostAsJsonAsync("/api/v1/hosted-strategies", new { name = "alpha", script_path = "x.py" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Host.Admin.PutAsJsonAsync($"/api/v1/hosted-strategies/{Guid.NewGuid()}/schedule", new { })).StatusCode);

        // Nothing changed on a refused update.
        var row = await RowAsync(s.Host, id);
        Assert.Equal(new TimeOnly(9, 15), row.ScheduleStartIst);
        Assert.Equal(TradingDays.Weekdays, row.ScheduleDays);
    }

    [PostgresFact]
    public async Task The_admin_token_is_required()
    {
        var s = New(Ist(11, 9, 15));
        await using var _ = s.Host;
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.Host.Anonymous().GetAsync("/api/v1/hosted-strategies")).StatusCode);
    }
}
