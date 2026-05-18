using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jesnote.Core;

/// <summary>
/// Selects how String node values are stored in <see cref="JsonTreeDocument"/>.
/// </summary>
public enum StringStorageMode
{
    /// <summary>
    /// Raw UTF-8 bytes packed into 16 MiB chunks. Roughly 40% less memory on
    /// CJK/long-text JSON than <see cref="Classic"/>, but every string read
    /// decodes UTF-8 to a fresh CLR string and full-document string search
    /// is ~2x slower because each candidate is decoded into a pooled char[].
    /// </summary>
    Compact,

    /// <summary>
    /// One CLR <see cref="string"/> per value in a parallel array - the
    /// pre-optimisation layout. Higher memory, zero-cost reads, regex
    /// search runs directly against the existing string instances.
    /// </summary>
    Classic,
}

/// <summary>
/// Storage strategy for the value of every <c>JsonNodeType.String</c> node.
/// The encoded <c>long</c> returned by <see cref="Append"/> is what
/// <see cref="JsonTreeDocument.Values"/>[id] holds for that node; only the
/// storage implementation knows what those bits mean.
///
/// Thread safety: writes happen on the parser thread; reads happen on the
/// UI thread after a published <see cref="JsonTreeDocument.Count"/> bump.
/// All implementations publish backing arrays via <see cref="Volatile.Write"/>
/// before any encoded ref that points into them becomes observable.
/// </summary>
public abstract class StringStorage
{
    /// <summary>Create the storage implementation for the given mode.</summary>
    public static StringStorage Create(StringStorageMode mode) =>
        mode switch
        {
            StringStorageMode.Classic => new PooledStringStorage(),
            _ => new Utf8ChunkStringStorage(),
        };

    /// <summary>Copy the current String token from the reader into storage and return the encoded ref.</summary>
    public abstract long Append(ref Utf8JsonReader reader);

    /// <summary>Decode a previously-appended value to a CLR string.</summary>
    public abstract string Get(long encoded);

    /// <summary>
    /// Decode at most <paramref name="maxChars"/> characters. Sets
    /// <paramref name="truncated"/> to true if the value was longer and the
    /// caller should append an ellipsis marker.
    /// </summary>
    public abstract string GetCapped(long encoded, int maxChars, out bool truncated);

    /// <summary>Run a regex against the value without forcing a permanent string allocation.</summary>
    public abstract bool Match(long encoded, Regex rx);

    /// <summary>Emit the value through a <see cref="Utf8JsonWriter"/> (property or value position).</summary>
    public abstract void Write(Utf8JsonWriter writer, string? propertyName, long encoded);

    /// <summary>Drop all stored data; storage is reusable after this.</summary>
    public abstract void Reset();
}

/// <summary>
/// Default storage. Raw UTF-8 bytes live in append-only 16 MiB chunks; for
/// each String node we pack (chunk, offset, length) into a single 64-bit
/// value that JsonTreeDocument stores in its Values[] slot. No CLR string
/// instance is created during parse; reads decode on demand.
///
/// Encoded layout (low to high):
///   bits  0..23 : length in bytes     (0..16 MiB)
///   bits 24..47 : offset within chunk (0..16 MiB)
///   bits 48..63 : chunk index         (0..65535)
/// Total addressable string storage: 65536 chunks * 16 MiB = 1 TiB.
/// Empty string is the sentinel encoding 0L and bypasses chunk allocation.
/// </summary>
public sealed class Utf8ChunkStringStorage : StringStorage
{
    const int ChunkSizeBits = 24;
    const int ChunkSizeBytes = 1 << ChunkSizeBits; // 16 MiB
    const long FieldMask = (1L << ChunkSizeBits) - 1; // 24-bit mask
    const int OffsetShift = 24;
    const int ChunkShift = 48;
    const int MaxChunks = 1 << 16;
    const int CappedDecodeRatio = 4; // UTF-8 byte / UTF-16 char worst case

    byte[][] _chunks = [];
    int _currentOffset;

    public override long Append(ref Utf8JsonReader reader)
    {
        int worst = reader.HasValueSequence
            ? checked((int)reader.ValueSequence.Length)
            : reader.ValueSpan.Length;
        if (worst == 0)
            return 0L;

        var (chunk, offset, chunkIdx) = Reserve(worst);
        int written = reader.CopyString(chunk.AsSpan(offset, worst));
        if (written < worst)
        {
            // JSON-escape unescaping shrank the byte count; give the tail back.
            _currentOffset -= worst - written;
        }
        return Encode(chunkIdx, offset, written);
    }

    public override string Get(long encoded)
    {
        if (encoded == 0)
            return string.Empty;
        Decode(encoded, out int chunkIdx, out int offset, out int length);
        var chunks = Volatile.Read(ref _chunks);
        return Encoding.UTF8.GetString(chunks[chunkIdx], offset, length);
    }

    public override string GetCapped(long encoded, int maxChars, out bool truncated)
    {
        if (encoded == 0)
        {
            truncated = false;
            return string.Empty;
        }
        Decode(encoded, out int chunkIdx, out int offset, out int length);
        var chunks = Volatile.Read(ref _chunks);

        // Bound the decode at maxChars * 4 bytes (UTF-8 max bytes per char)
        // so a multi-MB string isn't fully materialised just to be truncated.
        int probeBytes =
            length <= CappedDecodeRatio * maxChars ? length : CappedDecodeRatio * maxChars;
        string s = Encoding.UTF8.GetString(chunks[chunkIdx], offset, probeBytes);
        if (probeBytes == length && s.Length <= maxChars)
        {
            truncated = false;
            return s;
        }
        truncated = true;
        return s.Length <= maxChars ? s : s[..maxChars];
    }

    public override bool Match(long encoded, Regex rx)
    {
        if (encoded == 0)
            return rx.IsMatch(string.Empty);
        Decode(encoded, out int chunkIdx, out int offset, out int length);
        var chunks = Volatile.Read(ref _chunks);
        var bytes = chunks[chunkIdx].AsSpan(offset, length);

        // Decode into a pooled char[] - avoids allocating a permanent CLR
        // string per candidate during full-document scans (~30M strings).
        int charCount = Encoding.UTF8.GetCharCount(bytes);
        char[] buf = ArrayPool<char>.Shared.Rent(charCount);
        try
        {
            int written = Encoding.UTF8.GetChars(bytes, buf.AsSpan());
            return rx.IsMatch(buf.AsSpan(0, written));
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buf);
        }
    }

    public override void Write(Utf8JsonWriter writer, string? propertyName, long encoded)
    {
        // Skip the UTF-16 round-trip - Utf8JsonWriter accepts raw UTF-8 spans
        // and handles JSON metachar escaping on the byte stream directly.
        ReadOnlySpan<byte> bytes;
        if (encoded == 0)
        {
            bytes = ReadOnlySpan<byte>.Empty;
        }
        else
        {
            Decode(encoded, out int chunkIdx, out int offset, out int length);
            bytes = _chunks[chunkIdx].AsSpan(offset, length);
        }
        if (propertyName != null)
            writer.WriteString(propertyName, bytes);
        else
            writer.WriteStringValue(bytes);
    }

    public override void Reset()
    {
        Volatile.Write(ref _chunks, []);
        _currentOffset = 0;
    }

    (byte[] chunk, int offset, int chunkIdx) Reserve(int needed)
    {
        if (needed > ChunkSizeBytes)
            throw new InvalidDataException(
                $"String value of {needed:N0} bytes exceeds the {ChunkSizeBytes / (1024 * 1024)} MiB single-string limit."
            );

        var chunks = _chunks;
        if (chunks.Length == 0 || _currentOffset + needed > chunks[^1].Length)
        {
            if (chunks.Length >= MaxChunks)
                throw new InvalidDataException(
                    $"Document exceeds the {(long)MaxChunks * ChunkSizeBytes / (1024L * 1024 * 1024)} GiB string-storage limit."
                );
            var newChunks = new byte[chunks.Length + 1][];
            Array.Copy(chunks, newChunks, chunks.Length);
            newChunks[^1] = new byte[ChunkSizeBytes];
            // Publish chunk array BEFORE any encoded ref pointing into it
            // becomes observable, so UI-thread readers always see the chunk.
            Volatile.Write(ref _chunks, newChunks);
            chunks = newChunks;
            _currentOffset = 0;
        }
        int idx = chunks.Length - 1;
        int off = _currentOffset;
        _currentOffset += needed;
        return (chunks[idx], off, idx);
    }

    static long Encode(int chunkIdx, int offset, int length) =>
        ((long)chunkIdx << ChunkShift) | ((long)offset << OffsetShift) | (long)length;

    static void Decode(long v, out int chunkIdx, out int offset, out int length)
    {
        length = (int)(v & FieldMask);
        offset = (int)((v >> OffsetShift) & FieldMask);
        chunkIdx = (int)((v >> ChunkShift) & 0xFFFF);
    }
}

/// <summary>
/// Pre-optimisation storage. One CLR string per value in a parallel array,
/// grown by doubling. The encoded ref is just the int index. Higher memory
/// but every read is a free reference return - no decode, no allocation,
/// no extra work in regex search.
/// </summary>
public sealed class PooledStringStorage : StringStorage
{
    const int InitialCapacity = 16 * 1024;

    string[] _pool = [];
    int _count;
    int _capacity;

    public override long Append(ref Utf8JsonReader reader)
    {
        string value = reader.GetString() ?? string.Empty;
        EnsureCapacity(_count + 1);
        int idx = _count++;
        _pool[idx] = value;
        return idx;
    }

    public override string Get(long encoded) => Volatile.Read(ref _pool)[(int)encoded];

    public override string GetCapped(long encoded, int maxChars, out bool truncated)
    {
        string s = Get(encoded);
        if (s.Length <= maxChars)
        {
            truncated = false;
            return s;
        }
        truncated = true;
        return s[..maxChars];
    }

    public override bool Match(long encoded, Regex rx) => rx.IsMatch(Get(encoded));

    public override void Write(Utf8JsonWriter writer, string? propertyName, long encoded)
    {
        string s = Get(encoded);
        if (propertyName != null)
            writer.WriteString(propertyName, s);
        else
            writer.WriteStringValue(s);
    }

    public override void Reset()
    {
        Volatile.Write(ref _pool, []);
        _count = 0;
        _capacity = 0;
    }

    void EnsureCapacity(int needed)
    {
        if (needed <= _capacity)
            return;
        int newCap = _capacity == 0 ? InitialCapacity : _capacity;
        while (newCap < needed)
            newCap *= 2;
        // Array.Resize allocates a new array and copies; the field swap is atomic,
        // so readers see either the old or new (both valid for indices < _count).
        // The Volatile.Write makes the new ref publish.
        Array.Resize(ref _pool, newCap);
        Volatile.Write(ref _pool, _pool);
        _capacity = newCap;
    }
}
