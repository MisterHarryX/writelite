namespace WriteLite.Services;

public sealed class AsyncOperationGate
{
    private int _isRunning;

    public bool IsRunning => Volatile.Read(ref _isRunning) == 1;

    public bool TryEnter()
    {
        return Interlocked.CompareExchange(ref _isRunning, 1, 0) == 0;
    }

    public void Exit()
    {
        Volatile.Write(ref _isRunning, 0);
    }
}
