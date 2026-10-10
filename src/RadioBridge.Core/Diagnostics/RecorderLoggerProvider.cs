using Microsoft.Extensions.Logging;

namespace RadioBridge.Core.Diagnostics;

/// <summary>Copies log messages into the debug recording while one runs, so the file tells the whole story.</summary>
public sealed class RecorderLoggerProvider(Recorder recorder) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new RecorderLogger(recorder, ShortName(categoryName));

    public void Dispose() { }

    // "RadioBridge.Core.Sim.XPlane.XPlaneBackend" -> "XPlaneBackend"
    private static string ShortName(string category) => category[(category.LastIndexOf('.') + 1)..];

    private sealed class RecorderLogger(Recorder recorder, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => recorder.IsRecording && logLevel >= LogLevel.Debug;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;
            string message = formatter(state, exception);
            recorder.Record("log", logLevel.ToString().ToLowerInvariant(), w =>
            {
                w.WriteString("category", category);
                w.WriteString("message", message);
                if (exception is not null)
                    w.WriteString("exception", exception.ToString());
            });
        }
    }
}
