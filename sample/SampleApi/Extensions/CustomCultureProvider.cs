using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Primitives;

namespace SampleApi.Extensions;

public sealed class CustomCultureProvider(IEnumerable<IRequestCultureProvider> providers)
    : IRequestCultureProvider
{
    private readonly IReadOnlyList<IRequestCultureProvider> _providers = [.. providers];

    public async Task<ProviderCultureResult?> DetermineProviderCultureResult(
        HttpContext httpContext
    )
    {
        ProviderCultureResult? result = null;

        using (var enumerator = _providers.GetEnumerator())
            while (result is null && enumerator.MoveNext())
                result = await enumerator.Current.DetermineProviderCultureResult(httpContext);

        if (result is not null)
            result = new(
                [.. result.Cultures.Select(Normalize)],
                [.. result.UICultures.Select(Normalize)]
            );

        return result;
    }

    private static StringSegment Normalize(StringSegment culture) =>
        culture.Value switch
        {
            var s when s?.EndsWith("-CA", StringComparison.OrdinalIgnoreCase) ?? false => culture,
            "en" or "fr" => $"{culture}-CA",
            _ => culture,
        };
}
