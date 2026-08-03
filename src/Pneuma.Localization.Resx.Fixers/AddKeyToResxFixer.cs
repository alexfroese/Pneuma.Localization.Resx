using System.Collections.Immutable;
using System.Text;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Text;
using Pneuma.Localization.Resx.Generators;

namespace Pneuma.Localization.Resx.Fixers;

[ExportCodeFixProvider(LanguageNames.CSharp)]
public class AddKeyToResxFixer : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds =>
        [StronglyTypedResxAnalyzer.AddKeyToResourceFile];

    public override FixAllProvider? GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var project = context.Document.Project;
        var solution = project.Solution;

        if (
            !project.AnalyzerOptions.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue(
                "build_property.MSBuildProjectDirectory",
                out var projectDir
            )
        )
            return Task.CompletedTask;

        foreach (var diagnostic in context.Diagnostics)
        {
            if (
                !diagnostic.Properties.TryGetValue("fileName", out var fileName) || fileName is null
            )
                continue;
            if (!diagnostic.Properties.TryGetValue("key", out var key) || key is null)
                continue;

            if (
                project.AdditionalDocuments.FirstOrDefault(f =>
                    f.FilePath == Path.Combine(projectDir, fileName)
                )
                is not TextDocument document
            )
                continue;

            context.RegisterCodeFix(
                CodeAction.Create(
                    "Add key to resx",
                    ct => Fix(solution, project.Id, document, key, ct),
                    equivalenceKey: nameof(AddKeyToResxFixer)
                ),
                diagnostic
            );
        }

        return Task.CompletedTask;
    }

    private async Task<Solution> Fix(
        Solution solution,
        ProjectId projectId,
        TextDocument textDocument,
        string key,
        CancellationToken cancellationToken
    )
    {
        var project = solution.GetProject(projectId);

        if (project is null)
            return solution;

        var sourceText = await textDocument.GetTextAsync(cancellationToken);

        var xml = XDocument.Parse(sourceText.ToString(), LoadOptions.PreserveWhitespace);

        var root = xml.Root;

        var newLine = sourceText.ToString().Contains("\r\n") ? "\r\n" : "\n";

        var lastData = root.Elements("data").LastOrDefault();

        if (lastData is null)
        {
            var newElement = new XElement(
                "data",
                new XAttribute("name", key),
                new XAttribute(XNamespace.Xml + "space", "preserve"),
                newLine,
                "    ",
                new XElement("value", key)
            );

            root.Add(newLine, "  ", newElement, newLine);
        }
        else
        {
            var newElement = new XElement(lastData);
            newElement.Attribute("name").SetValue(key);
            newElement.Element("value").SetValue(key);
            lastData.AddAfterSelf(newLine, "  ", newElement);
        }

        using var writer = new StringWriterWithEncoding(sourceText.Encoding ?? Encoding.UTF8);

        xml.Save(writer, SaveOptions.None);

        var newSource = SourceText.From(
            writer.ToString(),
            writer.Encoding,
            sourceText.ChecksumAlgorithm
        );

        return solution.WithAdditionalDocumentText(textDocument.Id, newSource);
    }

    private class StringWriterWithEncoding(Encoding encoding) : StringWriter
    {
        public override Encoding Encoding => encoding;
    }
}
