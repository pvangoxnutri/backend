using System.Security.Cryptography;

namespace sidequest.backend.Services.Gluno;

public enum GlunoDocumentFormat
{
    Unsupported,
    Pdf,
    Jpeg,
    Png,
    WebP,
}

public sealed record GlunoFileCheck(
    GlunoDocumentFormat Format,
    bool IsSupported,
    /// Machine code when rejected: "unsupported_format", "too_large",
    /// "encrypted_pdf", "corrupt", "empty".
    string? RejectionCode)
{
    /// SHA-256 of the bytes. The idempotency key for analysis — the same file
    /// is never read twice, and a changed file is always read again.
    public string? Sha256 { get; init; }

    public string MediaType => Format switch
    {
        GlunoDocumentFormat.Pdf => "application/pdf",
        GlunoDocumentFormat.Jpeg => "image/jpeg",
        GlunoDocumentFormat.Png => "image/png",
        GlunoDocumentFormat.WebP => "image/webp",
        _ => "application/octet-stream",
    };
}

/// <summary>
/// Decides what a file actually IS, from its bytes.
///
/// WHY NOT THE FILENAME OR THE CONTENT-TYPE. Both are attacker-controlled. A
/// file called <c>booking.pdf</c> with an HTML payload inside, uploaded with
/// <c>Content-Type: application/pdf</c>, passes every check that trusts
/// metadata — and then gets handed to a parser that was not written for it.
/// The first bytes of a file are the only part that cannot lie about the
/// format, so that is what is read.
///
/// The rejections are as important as the acceptances. SVG and HTML are
/// refused outright: both are executable-ish document formats with script and
/// external-reference capabilities, and neither is a booking confirmation.
/// </summary>
public static class GlunoDocumentFile
{
    /// <summary>
    /// Magic bytes. Short prefixes on purpose — a longer match would reject
    /// legitimate variants without catching anything a short one misses.
    /// </summary>
    private static readonly (byte[] Signature, GlunoDocumentFormat Format)[] Signatures =
    [
        ("%PDF"u8.ToArray(), GlunoDocumentFormat.Pdf),
        ([0xFF, 0xD8, 0xFF], GlunoDocumentFormat.Jpeg),
        ([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], GlunoDocumentFormat.Png),
    ];

    /// <summary>
    /// Formats that must never be accepted, whatever they are called.
    ///
    /// SVG is XML with script and external references. HTML is worse. A
    /// Windows executable in a document library is not a mistake anyone makes
    /// by accident.
    /// </summary>
    private static readonly (byte[] Signature, string Code)[] Forbidden =
    [
        ("<?xml"u8.ToArray(), "unsupported_format"),
        ("<svg"u8.ToArray(), "unsupported_format"),
        ("<!DOCTYPE"u8.ToArray(), "unsupported_format"),
        ("<html"u8.ToArray(), "unsupported_format"),
        ("MZ"u8.ToArray(), "unsupported_format"),
        ([0x7F, 0x45, 0x4C, 0x46], "unsupported_format"),
        ("PK\x03\x04"u8.ToArray(), "unsupported_format"),
    ];

    /// <summary>
    /// Inspects the bytes and decides whether this file may be analysed.
    /// </summary>
    public static GlunoFileCheck Inspect(ReadOnlySpan<byte> content, long maxSizeBytes)
    {
        if (content.Length == 0)
            return new GlunoFileCheck(GlunoDocumentFormat.Unsupported, false, "empty");

        if (content.Length > maxSizeBytes)
            return new GlunoFileCheck(GlunoDocumentFormat.Unsupported, false, "too_large");

        // Forbidden shapes first. A file that is BOTH (an HTML page whose
        // bytes happen to begin with something else) should fail on the
        // dangerous reading, not the convenient one.
        foreach (var (signature, code) in Forbidden)
        {
            if (StartsWith(content, signature))
                return new GlunoFileCheck(GlunoDocumentFormat.Unsupported, false, code);
        }

        var format = Detect(content);

        if (format == GlunoDocumentFormat.Unsupported)
            return new GlunoFileCheck(GlunoDocumentFormat.Unsupported, false, "unsupported_format");

        if (format == GlunoDocumentFormat.Pdf)
        {
            if (IsEncryptedPdf(content))
                return new GlunoFileCheck(format, false, "encrypted_pdf");

            // A PDF without an EOF marker was truncated in transit or on disk.
            // Handing it to a parser produces a partial read that looks like a
            // successful one, which is the worst outcome available.
            if (!HasPdfTrailer(content))
                return new GlunoFileCheck(format, false, "corrupt");
        }

        return new GlunoFileCheck(format, true, null) { Sha256 = Hash(content) };
    }

    private static GlunoDocumentFormat Detect(ReadOnlySpan<byte> content)
    {
        foreach (var (signature, format) in Signatures)
        {
            if (StartsWith(content, signature)) return format;
        }

        // RIFF....WEBP — the format id sits at offset 8, so it needs its own
        // check rather than a prefix match.
        if (content.Length >= 12
            && StartsWith(content, "RIFF"u8)
            && content[8] == (byte)'W' && content[9] == (byte)'E'
            && content[10] == (byte)'B' && content[11] == (byte)'P')
        {
            return GlunoDocumentFormat.WebP;
        }

        return GlunoDocumentFormat.Unsupported;
    }

    /// <summary>
    /// An /Encrypt entry in the trailer means the content is not readable
    /// without a password we do not have.
    ///
    /// Detected rather than attempted: a failed decrypt deep inside a parser
    /// surfaces as a vague error, while this produces a clear "we can't open
    /// this one" the user can act on.
    /// </summary>
    private static bool IsEncryptedPdf(ReadOnlySpan<byte> content)
    {
        // The trailer lives at the end. Scanning the tail is both cheaper and
        // more accurate than scanning a whole document for a common token.
        var tailLength = Math.Min(content.Length, 4096);
        var tail = content[^tailLength..];

        return IndexOf(tail, "/Encrypt"u8) >= 0;
    }

    private static bool HasPdfTrailer(ReadOnlySpan<byte> content)
    {
        var tailLength = Math.Min(content.Length, 2048);
        return IndexOf(content[^tailLength..], "%%EOF"u8) >= 0;
    }

    /// <summary>
    /// A server-generated temporary name.
    ///
    /// Nothing from the document, the upload, or the user reaches the
    /// filesystem — the name is a fresh Guid under a known directory, so path
    /// traversal is impossible by construction rather than by sanitising.
    /// </summary>
    public static string TemporaryPath(string directory, GlunoDocumentFormat format)
    {
        var extension = format switch
        {
            GlunoDocumentFormat.Pdf => ".pdf",
            GlunoDocumentFormat.Jpeg => ".jpg",
            GlunoDocumentFormat.Png => ".png",
            GlunoDocumentFormat.WebP => ".webp",
            _ => ".bin",
        };

        return Path.Combine(directory, $"gluno-{Guid.NewGuid():N}{extension}");
    }

    public static string Hash(ReadOnlySpan<byte> content)
        => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    /// A coarse bucket for telemetry — the exact size of somebody's booking
    /// confirmation is not information a log needs.
    public static string SizeBucket(long bytes) => bytes switch
    {
        < 100 * 1024 => "tiny",
        < 1024 * 1024 => "small",
        < 5L * 1024 * 1024 => "medium",
        _ => "large",
    };

    public static string PageBucket(int pages) => pages switch
    {
        <= 1 => "1",
        <= 3 => "2-3",
        <= 8 => "4-8",
        _ => "9+",
    };

    private static bool StartsWith(ReadOnlySpan<byte> content, ReadOnlySpan<byte> prefix)
        => content.Length >= prefix.Length && content[..prefix.Length].SequenceEqual(prefix);

    private static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
        => haystack.IndexOf(needle);
}
