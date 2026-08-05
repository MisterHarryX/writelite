namespace WriteLite.Services;

public static class TextWriteTelemetry
{
    private static int _writeCount;

    public static int WriteCount => Volatile.Read(ref _writeCount);

    public static void RecordWrite()
    {
        Interlocked.Increment(ref _writeCount);
    }

    public static void Reset()
    {
        Interlocked.Exchange(ref _writeCount, 0);
    }
}
