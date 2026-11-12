namespace EPTradeBot.Core.Portfolio;

/// <summary>The requested SIP schedule cannot be built.</summary>
public sealed class ScheduleException(string message) : Exception(message);

/// <summary>One scheduled investment: the calendar date asked for, the trading session it landed on, and the amount.</summary>
public sealed record Installment(DateOnly Requested, DateOnly Executed, double Amount);

/// <summary>The dates and amounts of a SIP. OpenAlgo's sip.schedule: a date that is a holiday executes on the next session, and an installment whose session falls past the end is dropped.</summary>
public static class SipSchedule
{
    public static readonly string[] Frequencies = ["monthly", "fortnightly", "weekly", "quarterly"];

    /// <summary>Dates after the 28th are refused rather than clamped: they do not exist in every month.</summary>
    public const int MaxDayOfMonth = 28;

    private static DateOnly MonthAdd(DateOnly anchor, int months)
    {
        var total = anchor.Month - 1 + months;
        var year = anchor.Year + (int)Math.Floor(total / 12.0);
        var month = ((total % 12) + 12) % 12 + 1;
        return new DateOnly(year, month, anchor.Day);
    }

    public static List<DateOnly> RequestedDates(DateOnly start, DateOnly end, string frequency = "monthly", int dayOfMonth = 1)
    {
        if (!Frequencies.Contains(frequency)) throw new ScheduleException($"frequency must be one of {string.Join(", ", Frequencies)}, got '{frequency}'");
        if (end < start) throw new ScheduleException("end date is before start date");
        if (frequency is "monthly" or "quarterly")
        {
            if (dayOfMonth is < 1 or > MaxDayOfMonth)
                throw new ScheduleException($"day_of_month must be between 1 and {MaxDayOfMonth}; days after the 28th do not exist in every month");
            var step = frequency == "monthly" ? 1 : 3;
            var anchor = new DateOnly(start.Year, start.Month, dayOfMonth);
            if (anchor < start) anchor = MonthAdd(anchor, step);
            var dates = new List<DateOnly>();
            for (var i = 0; ; i++)
            {
                var when = MonthAdd(anchor, i * step);
                if (when > end) break;
                dates.Add(when);
            }

            return dates;
        }

        var delta = frequency == "weekly" ? 7 : 14;
        var result = new List<DateOnly>();
        for (var when = start; when <= end; when = when.AddDays(delta)) result.Add(when);
        return result;
    }

    /// <summary>Each amount raised by the step-up percent on every anniversary of the first installment.</summary>
    public static List<double> ApplyStepUp(IReadOnlyList<DateOnly> dates, double baseAmount, double stepUpPercent = 0)
    {
        if (dates.Count == 0) return [];
        if (stepUpPercent < 0) throw new ScheduleException("step_up_percent cannot be negative");
        var first = dates[0];
        return dates.Select(d => baseAmount * Math.Pow(1.0 + stepUpPercent / 100.0, (d.DayNumber - first.DayNumber) / 365)).ToList();
    }

    public static List<Installment> Build(IReadOnlyList<DateOnly> sessions, DateOnly start, DateOnly end, double amount, string frequency = "monthly", int dayOfMonth = 1, double stepUpPercent = 0)
    {
        if (amount <= 0) throw new ScheduleException("SIP amount must be positive");
        if (sessions.Count == 0) throw new ScheduleException("no trading sessions available for this period");
        var wanted = RequestedDates(start, end, frequency, dayOfMonth);
        if (wanted.Count == 0) throw new ScheduleException("the SIP window is too short to contain a single installment");
        var amounts = ApplyStepUp(wanted, amount, stepUpPercent);
        var result = new List<Installment>();
        var cursor = 0;
        for (var i = 0; i < wanted.Count; i++)
        {
            while (cursor < sessions.Count && sessions[cursor] < wanted[i]) cursor++;
            if (cursor >= sessions.Count) break;
            var executed = sessions[cursor];
            if (executed > end) break;
            result.Add(new Installment(wanted[i], executed, amounts[i]));
        }

        if (result.Count == 0) throw new ScheduleException("no SIP installment could be placed on a trading session in this window");
        return result;
    }
}
