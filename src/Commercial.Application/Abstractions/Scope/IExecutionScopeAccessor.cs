namespace Commercial.Application.Abstractions.Scope;

public interface IExecutionScopeAccessor
{
    /// <summary>The active scope, or <see cref="ExecutionScope.Unscoped"/>.</summary>
    ExecutionScope Current { get; }

    /// <summary>Establishes <paramref name="scope"/> for the current async flow until disposed.</summary>
    IDisposable Begin(ExecutionScope scope);
}

/// <summary>AsyncLocal-backed accessor; behaves identically for HTTP requests and background jobs.</summary>
public sealed class AmbientExecutionScopeAccessor : IExecutionScopeAccessor
{
    private static readonly AsyncLocal<ExecutionScope?> Ambient = new();

    public ExecutionScope Current => Ambient.Value ?? ExecutionScope.Unscoped;

    public IDisposable Begin(ExecutionScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var previous = Ambient.Value;
        Ambient.Value = scope;
        return new Restore(previous);
    }

    private sealed class Restore(ExecutionScope? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            Ambient.Value = previous;
            _disposed = true;
        }
    }
}
