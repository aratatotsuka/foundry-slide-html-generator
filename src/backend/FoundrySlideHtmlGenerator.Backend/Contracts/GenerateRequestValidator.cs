namespace FoundrySlideHtmlGenerator.Backend.Contracts;

public static class GenerateRequestValidator
{
    public static (bool Ok, string? Error) Validate(GenerateRequest request)
    {
        if (request is null)
        {
            return (false, "Body is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            return (false, "prompt is required.");
        }

        if (request.Prompt.Length > 10_000)
        {
            return (false, "prompt is too long (max 10000 chars).");
        }

        if (!IsValidAspect(request.Aspect))
        {
            return (false, "aspect must be \"16:9\" or \"4:3\".");
        }

        if (request.ImageBase64 is { Length: > 12_000_000 })
        {
            return (false, "imageBase64 is too large.");
        }

        return (true, null);
    }

    public static bool IsValidAspect(string? aspect) => aspect is "16:9" or "4:3";
}

