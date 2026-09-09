using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Localization;
using NodaTime;
using SampleApi;
using SampleApi.Extensions;
using SampleApi.Some.Deeply.Nested;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.AddLocalization(o => o.ResourcesPath = "Resources");

builder.Services.AddRequestLocalization(
    (options) =>
    {
        options.DefaultRequestCulture = new("en-CA");

        options.SupportedCultures = [new("en-CA"), new("fr-CA")];
        options.SupportedUICultures = [new("en-CA"), new("fr-CA")];

        options.ApplyCurrentCultureToResponseHeaders = true;

        options.RequestCultureProviders =
        [
            new CustomCultureProvider(options.RequestCultureProviders),
        ];
    }
);

builder
    .Services.AddHealthChecks()
    .AddCheck("api", () => HealthCheckResult.Healthy(), tags: ["api"]);

builder.Services.AddTransient<Service>();

builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseRequestLocalization();

app.MapHealthChecks(
    "/health",
    new()
    {
        ResponseWriter = (context, report) =>
            context.Response.WriteAsJsonAsync(report, JsonSerializerOptions.Web),
    }
);

app.MapGet(
    "hello",
    ([FromServices] IStringLocalizer<Hello> localizer) => TypedResults.Ok(localizer.Whatever)
);

app.MapGet(
    "from-program",
    ([FromServices] IStringLocalizer<Program> localizer) =>
        TypedResults.Ok(localizer.Formattable_now(12345, 67890))
);

app.MapGet(
    "with-dates",
    ([FromServices] IStringLocalizer<Program> localizer) =>
        TypedResults.Ok(
            localizer.Formattable_dates(new LocalDate(1990, 8, 5), new LocalDate(1993, 10, 27))
        )
);

app.MapGet(
    "from-service",
    ([FromServices] Service service) => TypedResults.Ok(service.GetString())
);

app.MapGet(
    "with-param",
    ([FromServices] IStringLocalizer<Hello> localizer, [FromQuery] string test) =>
        TypedResults.Ok(localizer.One_with_an_argument(test))
);

await app.RunAsync();

namespace SampleApi
{
    public sealed class Hello;
}
