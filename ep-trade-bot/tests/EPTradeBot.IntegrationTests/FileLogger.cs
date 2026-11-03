using Microsoft.Extensions.Logging;

namespace EPTradeBot.IntegrationTests;

/// <summary>Writes a host's log to a file, for diagnosing a test.</summary>
public sealed class FileLoggerProvider(string path) : ILoggerProvider
{
    private readonly object gate = new();

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
    }

    private void Write(string line)
    {
        lock (gate) File.AppendAllText(path, line + Environment.NewLine);
    }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            owner.Write($"{logLevel} {category}: {formatter(state, exception)} {exception}");
    }
}
