using Microsoft.Extensions.Localization;

namespace SampleApi.Some.Deeply.Nested;

public class Service(IStringLocalizer<Service> localizer)
{
    private readonly IStringLocalizer<Service> _localizer = localizer;

    public string GetString() => _localizer.Something;
}
