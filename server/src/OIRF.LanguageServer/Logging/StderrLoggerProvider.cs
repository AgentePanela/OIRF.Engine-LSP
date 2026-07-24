using Microsoft.Extensions.Logging;

namespace OIRF.LanguageServer.Logging;

// stdout is reserved for LSP JSON-RPC framing when the server runs over the stdio transport -
// any stray Console.WriteLine there corrupts the protocol stream, so all diagnostic logging
// must go to stderr instead.
public sealed class StderrLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new StderrLogger(categoryName);

    public void Dispose() { }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }

    private sealed class StderrLogger(string categoryName) : ILogger
    {
        // Must NOT return null: OmniSharp's internal request-timing logger (TimeLoggerExtensions)
        // calls .Dispose() on whatever BeginScope returns without a null check, and crashes the
        // whole request pipeline with a NullReferenceException if it's null.
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var message = formatter(state, exception);
            Console.Error.WriteLine($"[{logLevel}] {categoryName}: {message}");
            if (exception is not null)
                Console.Error.WriteLine(exception);
        }
    }
}
