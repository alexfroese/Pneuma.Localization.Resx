# Pneuma.Localization.Resx

Roslyn source generators and code fixes for strongly typed localization from `.resx` resources in .NET applications.

## What it does

The analyzer discovers `.resx` files included in a project and generates strongly typed extension APIs for `IStringLocalizer<T>`. It also provides code fixes for supported resource diagnostics. Nested resource paths and localized resource variants are supported.

## Projects

- `src/Pneuma.Localization.Resx.Generators` — source generator and analyzer implementation.
- `src/Pneuma.Localization.Resx.Fixers` — Roslyn code fixes and NuGet packaging support.
- `tests/Pneuma.Localization.UnitTests` — generator tests using TUnit and Roslyn testing libraries.
- `sample/SampleApi` — example ASP.NET application with resources under `Resources/`.

## Requirements

- .NET SDK `10.0.302` or a compatible SDK selected by `global.json`.

## Build and test

From the repository root:

```bash
dotnet restore Pneuma.Localization.Resx.slnx
dotnet build Pneuma.Localization.Resx.slnx
dotnet test tests/Pneuma.Localization.UnitTests/Pneuma.Localization.UnitTests.csproj
dotnet run --project sample/SampleApi/SampleApi.csproj
```

To create the analyzer package locally:

```bash
dotnet pack src/Pneuma.Localization.Resx.Fixers/Pneuma.Localization.Resx.Fixers.csproj -c Release
```

## Example

Add a resource such as `Resources/Hello.resx` to an application and reference the generator and fixer projects (or the published package). The generator produces a typed localizer extension for each resource key, allowing code such as:

```csharp
IStringLocalizer<Hello> localizer;
var message = localizer.Hello;
```

See `sample/SampleApi` for project configuration and nested-resource examples.

## License

This project is licensed under the MIT License. See [LICENSE](LICENSE).
