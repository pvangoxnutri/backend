namespace sidequest.backend.Services;

public sealed record ValidatedTripDocumentFile(string ContentType, string Extension);

public static class TripDocumentFileValidator
{
    private const int HeaderLength = 16;

    public static async Task<ValidatedTripDocumentFile?> DetectAsync(
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

        if (read >= 5
            && header[0] == (byte)'%'
            && header[1] == (byte)'P'
            && header[2] == (byte)'D'
            && header[3] == (byte)'F'
            && header[4] == (byte)'-')
        {
            return new("application/pdf", ".pdf");
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

        if (read >= 12
            && header.AsSpan(0, 4).SequenceEqual("RIFF"u8)
            && header.AsSpan(8, 4).SequenceEqual("WEBP"u8))
        {
            return new("image/webp", ".webp");
        }

        return null;
    }
}
