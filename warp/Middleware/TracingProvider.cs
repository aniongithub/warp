namespace Warp.Middleware;

public abstract class TracingProvider : IDisposable
{
    public string Name { get; }
    private bool _disposed;

    protected TracingProvider(string name)
    {
        Name = name;
    }

    public TraceSpan Start(string traceParent)
    {
        return _disposed ?
            throw new ObjectDisposedException(nameof(TracingProvider)) :
            CreateSpan(traceParent);
    }

    protected abstract TraceSpan CreateSpan(string traceParent);

    public void Dispose()
    {
        if (!_disposed)
        {
            try
            {
                OnDispose();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error during disposal: {ex}");
            }
            finally
            {
                _disposed = true;
            }
        }
    }

    protected abstract void OnDispose();
}

public abstract class TraceSpan : IDisposable
{
    private bool _disposed;
    private bool _stopped;

    public void SetTag(string key, string value) => OnSetTag(key, value);
    public void SetException(Exception ex) => OnSetException(ex);
    public void SetStatus(int status) => OnSetStatus(status);

    public void Stop()
    {
        if (_stopped) return;
        try
        {
            OnStop();
        }
        finally
        {
            _stopped = true;
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            try
            {
                if (!_stopped)
                {
                    try
                    {
                        OnStop();
                    }
                    catch (Exception ex)
                    {
                        OnSetException(ex);
                    }
                    _stopped = true;
                }
                OnDispose();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error during TraceSpan disposal: {ex}");
            }
            finally
            {
                _disposed = true;
            }
        }
    }

    protected abstract void OnSetTag(string key, string value);
    protected abstract void OnSetException(Exception ex);
    protected abstract void OnSetStatus(int status);
    protected abstract void OnStop();
    protected abstract void OnDispose();
}