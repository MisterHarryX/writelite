namespace WriteLite.Services.LanguageEngine;

/// <summary>
/// A request chunk may overlap its neighbours, while <see cref="OwnedStart"/>
/// and <see cref="OwnedLength"/> partition the document without gaps. Results
/// are assigned by their global UTF-16 start offset to exactly one owner.
/// </summary>
public sealed record SemanticTextChunk(
    int Index,
    int Start,
    string Text,
    int OwnedStart,
    int OwnedLength)
{
    public int End => Start + Text.Length;
    public int OwnedEnd => OwnedStart + OwnedLength;

    public bool Owns(int globalStart, int length, int documentLength)
    {
        if (globalStart < OwnedStart || globalStart > OwnedEnd)
        {
            return false;
        }

        // Only the final owner owns an insertion at end-of-document.
        if (globalStart == OwnedEnd)
        {
            return length == 0 && OwnedEnd == documentLength;
        }

        return globalStart + Math.Max(0, length) <= End;
    }
}

public sealed record SemanticChunkResult<T>(SemanticTextChunk Chunk, T Result);

/// <summary>
/// Produces semantic, overlapping chunks and executes them with bounded
/// concurrency. All offsets and coverage counts are UTF-16 code-unit indexes.
/// </summary>
public static class SemanticTextChunker
{
    public const int DefaultMaxConcurrency = 2;

    public static IReadOnlyList<SemanticTextChunk> CreatePlan(
        string text,
        int maxChunkLength,
        int? overlapLength = null)
    {
        text ??= string.Empty;
        if (text.Length == 0)
        {
            return [];
        }

        maxChunkLength = Math.Max(8, maxChunkLength);
        var overlap = overlapLength ?? Math.Min(512, Math.Max(1, maxChunkLength / 8));
        overlap = Math.Clamp(overlap, 1, Math.Max(1, (maxChunkLength - 2) / 3));
        var ownedTargetLength = Math.Max(2, maxChunkLength - (2 * overlap));

        var owners = new List<(int Start, int End)>();
        var ownedStart = 0;
        while (ownedStart < text.Length)
        {
            var hardEnd = Math.Min(text.Length, ownedStart + ownedTargetLength);
            var ownedEnd = hardEnd == text.Length
                ? text.Length
                : FindSemanticBreak(text, ownedStart, hardEnd);

            ownedEnd = AvoidSurrogateSplit(text, ownedEnd, preferEarlier: true);
            if (ownedEnd <= ownedStart)
            {
                ownedEnd = AvoidSurrogateSplit(
                    text,
                    Math.Min(text.Length, ownedStart + ownedTargetLength),
                    preferEarlier: false);
            }

            if (ownedEnd <= ownedStart)
            {
                // A valid UTF-16 scalar is at most two code units.
                ownedEnd = Math.Min(text.Length, ownedStart + 2);
            }

            owners.Add((ownedStart, ownedEnd));
            ownedStart = ownedEnd;
        }

        var chunks = new List<SemanticTextChunk>(owners.Count);
        for (var index = 0; index < owners.Count; index++)
        {
            var owner = owners[index];
            var requestStart = Math.Max(0, owner.Start - overlap);
            var requestEnd = Math.Min(text.Length, owner.End + overlap);

            requestStart = AvoidSurrogateSplit(text, requestStart, preferEarlier: false);
            requestEnd = AvoidSurrogateSplit(text, requestEnd, preferEarlier: true);

            // Semantic owner sizing guarantees this in normal cases. Keep the
            // transport contract strict even around a surrogate at an overlap edge.
            if (requestEnd - requestStart > maxChunkLength)
            {
                requestEnd = AvoidSurrogateSplit(
                    text,
                    requestStart + maxChunkLength,
                    preferEarlier: true);
            }

            if (requestEnd < owner.End)
            {
                requestEnd = owner.End;
                requestStart = Math.Max(0, requestEnd - maxChunkLength);
                requestStart = AvoidSurrogateSplit(text, requestStart, preferEarlier: false);
            }

            chunks.Add(new SemanticTextChunk(
                index,
                requestStart,
                text.Substring(requestStart, requestEnd - requestStart),
                owner.Start,
                owner.End - owner.Start));
        }

        return chunks;
    }

    public static async Task<IReadOnlyList<SemanticChunkResult<T>>> RunAsync<T>(
        IReadOnlyList<SemanticTextChunk> chunks,
        Func<SemanticTextChunk, CancellationToken, Task<T>> analyzeChunk,
        int maxConcurrency = DefaultMaxConcurrency,
        Action<int>? onOwnedCharactersChecked = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        ArgumentNullException.ThrowIfNull(analyzeChunk);
        if (chunks.Count == 0)
        {
            return [];
        }

        maxConcurrency = Math.Clamp(maxConcurrency, 1, 4);
        var results = new SemanticChunkResult<T>?[chunks.Count];
        await Parallel.ForEachAsync(
            chunks,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = maxConcurrency
            },
            async (chunk, token) =>
            {
                token.ThrowIfCancellationRequested();
                var value = await analyzeChunk(chunk, token).ConfigureAwait(false);
                results[chunk.Index] = new SemanticChunkResult<T>(chunk, value);
                onOwnedCharactersChecked?.Invoke(chunk.OwnedLength);
            }).ConfigureAwait(false);

        return results.Select(result => result!).ToArray();
    }

    private static int FindSemanticBreak(string text, int start, int hardEnd)
    {
        var minimum = start + Math.Max(1, (hardEnd - start) / 2);

        // Prefer paragraph boundaries, then sentence punctuation, then whitespace.
        for (var i = hardEnd - 1; i >= minimum; i--)
        {
            if (text[i] is '\n' or '\r')
            {
                return i + 1;
            }
        }

        for (var i = hardEnd - 1; i >= minimum; i--)
        {
            if (text[i] is '.' or '!' or '?' or ';' or '…')
            {
                return i + 1;
            }
        }

        for (var i = hardEnd - 1; i >= minimum; i--)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                return i + 1;
            }
        }

        return hardEnd;
    }

    private static int AvoidSurrogateSplit(string text, int boundary, bool preferEarlier)
    {
        boundary = Math.Clamp(boundary, 0, text.Length);
        if (boundary > 0 && boundary < text.Length
            && char.IsHighSurrogate(text[boundary - 1])
            && char.IsLowSurrogate(text[boundary]))
        {
            return preferEarlier ? boundary - 1 : boundary + 1;
        }

        return boundary;
    }
}
