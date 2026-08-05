namespace WriteLite.Services.LanguageEngine;

public interface IWriteLitePortAllocator
{
    /// <summary>
    /// Returns free loopback TCP ports starting from preferred, limited attempts.
    /// </summary>
    IEnumerable<int> AllocateCandidates(int preferredPort, int searchRange);
}
