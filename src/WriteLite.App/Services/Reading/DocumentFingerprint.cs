using System.IO;
using System.Security.Cryptography;

namespace WriteLite.Services.Reading;

/// <summary>
/// Identifies a book by what is in it rather than by where it is.
/// </summary>
/// <remarks>
/// A reading project has to survive its source file being renamed, moved to another
/// drive or copied to a laptop, and must not be re-created from scratch the second
/// time the same book is opened. Hashing the content answers both: the same bytes
/// give the same project no matter what the file is called.
///
/// Only the first few megabytes are hashed, with the file's length mixed in. Reading
/// a 400 MB scanned PDF in full would stall opening a book for seconds to distinguish
/// it from files it could not plausibly collide with, and the length makes a prefix
/// collision between two real documents vanishingly unlikely.
/// </remarks>
public static class DocumentFingerprint
{
    /// <summary>How much of the file is hashed. Large enough to pass any header and front matter.</summary>
    private static readonly int PrefixBytes = WriteLiteDefaults.Memory.DocumentFingerprintPrefixBytes;

    public static string Compute(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            // The book may be open in a reader elsewhere; identifying it must not
            // require exclusive access.
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024);

        return Compute(stream, stream.Length);
    }

    public static string Compute(Stream stream, long length)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        hash.AppendData(BitConverter.GetBytes(length));

        var buffer = new byte[64 * 1024];
        var remaining = PrefixBytes;

        while (remaining > 0)
        {
            var read = stream.Read(buffer, 0, Math.Min(buffer.Length, remaining));
            if (read <= 0)
            {
                break;
            }

            hash.AppendData(buffer, 0, read);
            remaining -= read;
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
