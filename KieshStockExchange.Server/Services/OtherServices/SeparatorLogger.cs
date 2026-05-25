using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public enum SeparatorPlacement { Before, After, Both }

public sealed class SeparatorLoggerOptions
{
    public string Separator { get; set; } = "";
    public SeparatorPlacement Placement { get; set; } = SeparatorPlacement.After;
    // Add an empty line after the entry
    public bool ExtraBlankLine { get; set; } = false;
}

public sealed class SeparatorLogger<T> : ILogger<T>
{
    #region Fields and Constructor
    private readonly ILogger _inner;
    private readonly SeparatorLoggerOptions _options;

    public SeparatorLogger(ILoggerFactory factory, IOptions<SeparatorLoggerOptions> options)
    {
        _inner = factory.CreateLogger(typeof(T).FullName!);
        _options = options.Value;
    }
    #endregion

    #region ILogger Implementation
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _inner.BeginScope(state);
    public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        _inner.Log(logLevel, eventId, state, exception, (s, e) =>
        {
            // Render the original formatted message
            var msg = formatter(s, e);

            // Build final text with separators/newlines
            var sb = new StringBuilder();
            var sep = _options.Separator;

            // Add separators before
            if (_options.Placement is SeparatorPlacement.Before or SeparatorPlacement.Both)
                sb.Append(sep).AppendLine();

            // Original message
            sb.Append(msg);

            // Add separators after
            if (_options.Placement is SeparatorPlacement.After or SeparatorPlacement.Both)
                sb.AppendLine().Append(sep);

            // Empty line after the provider's own newline
            if (_options.ExtraBlankLine)
                sb.AppendLine();

            return sb.ToString();
        });
    }
    #endregion
}
