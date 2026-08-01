namespace sidequest.backend.Services;

public sealed record ValidatedImageFile(string ContentType, string Extension);

/// <summary>
/// Decides what an uploaded image ACTUALLY is, from its bytes.
///
/// The upload endpoint previously trusted two client-controlled values: the
/// multipart Content-Type header, and the filename's extension. Both are set by
/// whoever makes the request, so neither says anything about the content. The
/// extension was the worse of the two — it was concatenated straight into the
/// storage object path, which made the stored object's name attacker-controlled
/// in a bucket that is served publicly.
///
/// Mirrors TripDocumentFileValidator, which already does this for documents,
/// minus PDF: only the four raster formats the app actually displays are
/// accepted. GIF is included because the old allow-list had it; SVG is
/// deliberately NOT, since it is markup and can carry script.
/// </summary>
public static class ImageFileValidator
{
    private const int HeaderLength = 16;

    public static async Task<ValidatedImageFile?> DetectAsync(
        Stream content,
        CancellationToken cancellationToken = default)
    {
        var header = new byte[HeaderLength];
        var read = 0;

        while (read < header.Length)
        {
            var current = await content.ReadAsync(header.AsMemory(read, header.Length - read), cancellationToken);
            if (current == 0) break;
            read += current;
        }

        if (read >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
        {
            return new("image/jpeg", ".jpg");
        }

        ReadOnlySpan<byte> pngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (read >= pngSignature.Length && header.AsSpan(0, pngSignature.Length).SequenceEqual(pngSignature))
        {
            return new("image/png", ".png");
        }

        // GIF87a / GIF89a.
        if (read >= 6
            && header.AsSpan(0, 3).SequenceEqual("GIF"u8)
            && (header.AsSpan(3, 3).SequenceEqual("87a"u8) || header.AsSpan(3, 3).SequenceEqual("89a"u8)))
        {
            return new("image/gif", ".gif");
        }

        if (read >= 12
            && header.AsSpan(0, 4).SequenceEqual("RIFF"u8)
            && header.AsSpan(8, 4).SequenceEqual("WEBP"u8))
        {
            return new("image/webp", ".webp");
        }

        return null;
    }
}
