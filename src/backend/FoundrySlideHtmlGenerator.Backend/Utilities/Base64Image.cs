namespace FoundrySlideHtmlGenerator.Backend.Utilities;

public static class Base64Image
{
    public static (bool Ok, string? Error, string? DataUrl) TryParse(string input, int maxBytes)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return (false, "imageBase64 is empty.", null);
        }

        var trimmed = input.Trim();
        if (trimmed.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            var commaIndex = trimmed.IndexOf(',');
            if (commaIndex < 0)
            {
                return (false, "imageBase64 data URL is invalid.", null);
            }

            var header = trimmed[..commaIndex];
            var payload = trimmed[(commaIndex + 1)..];

            if (!header.Contains(";base64", StringComparison.OrdinalIgnoreCase))
            {
                return (false, "imageBase64 data URL must be base64.", null);
            }

            var mime = header["data:".Length..].Split(';', 2)[0];
            if (!IsSupportedMime(mime))
            {
                return (false, "Only png/jpg images are supported.", null);
            }

            if (!TryDecode(payload, maxBytes, out _))
            {
                return (false, "imageBase64 could not be decoded.", null);
            }

            return (true, null, trimmed);
        }

        if (!TryDecode(trimmed, maxBytes, out var bytes))
        {
            return (false, "imageBase64 could not be decoded.", null);
        }

        var mimeType = DetectMime(bytes);
        if (mimeType is null)
        {
            return (false, "Only png/jpg images are supported.", null);
        }

        return (true, null, $"data:{mimeType};base64,{trimmed}");
    }

    private static bool IsSupportedMime(string mime)
        => mime.Equals("image/png", StringComparison.OrdinalIgnoreCase)
           || mime.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase)
           || mime.Equals("image/jpg", StringComparison.OrdinalIgnoreCase);

    private static string? DetectMime(ReadOnlySpan<byte> bytes)
    {
        // PNG signature
        if (bytes.Length >= 8 &&
            bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 &&
            bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
        {
            return "image/png";
        }

        // JPEG signature
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return "image/jpeg";
        }

        return null;
    }

    private static bool TryDecode(string base64, int maxBytes, out byte[] bytes)
    {
        // Fast pre-check to avoid huge allocations.
        // 4 chars -> 3 bytes, ignore padding.
        var estimated = base64.Length / 4 * 3;
        if (estimated > maxBytes)
        {
            bytes = Array.Empty<byte>();
            return false;
        }

        bytes = new byte[estimated];
        if (!Convert.TryFromBase64String(base64, bytes, out var written))
        {
            bytes = Array.Empty<byte>();
            return false;
        }

        if (written > maxBytes)
        {
            bytes = Array.Empty<byte>();
            return false;
        }

        if (written != bytes.Length)
        {
            Array.Resize(ref bytes, written);
        }

        return true;
    }
}
