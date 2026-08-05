namespace WriteLite.Services.Settings;

public interface IWriteLiteAutostartService
{
    bool IsEnabled { get; }
    bool CanEnableForCurrentBinary { get; }
    string StatusMessage { get; }
    void SetEnabled(bool enabled);
}
