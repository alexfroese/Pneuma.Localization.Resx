using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Localization;
using SampleApi;
using SampleApi.Some.Deeply.Nested;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.AddLocalization(o => o.ResourcesPath = "Resources");

builder.Services.AddRequestLocalization(options =>
{
    options.DefaultRequestCulture = new RequestCulture("en");

    options.SupportedCultures = [new CultureInfo("en"), new CultureInfo("fr")];
    options.SupportedUICultures = [new CultureInfo("en"), new CultureInfo("fr")];

    options.ApplyCurrentCultureToResponseHeaders = true;
});

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
    "from-service",
    ([FromServices] Service service) => TypedResults.Ok(service.GetString())
);

await app.RunAsync();

namespace SampleApi
{
    public sealed class Hello;
}
