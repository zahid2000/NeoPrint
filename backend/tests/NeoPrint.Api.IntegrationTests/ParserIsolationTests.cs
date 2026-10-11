using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Files;
using NeoPrint.Infrastructure.Conversion;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// The API process hands a user's file to no parser of its own (plan step 7,
/// T-051; docs/adr/ADR-0005-isolated-converter.md).
/// </summary>
/// <remarks>
/// The proof is in the compiled assemblies rather than in a code review:
/// nothing the API is made of refers to PDFium, SkiaSharp, PDFsharp, the
/// layout renderer or the converter tool itself, so there is no call it could
/// make into them. The tool - a process of its own - is the one assembly that
/// does. Somebody who adds such a call later fails this test.
/// </remarks>
public sealed class ParserIsolationTests
{
    /// <summary>Assemblies that parse documents and images.</summary>
    private static readonly string[] ParserAssemblies =
    [
        "SkiaSharp",
        "NeoPrint.Pdfium",
        "NeoPrint.Converter",
    ];

    /// <summary>PDFsharp is several assemblies; all of them.</summary>
    private const string PdfSharpPrefix = "PdfSharp";

    /// <summary>
    /// <c>NeoPrint.Layout</c> is shared: the API reads a layout's JSON with it.
    /// Only this type decodes pictures and writes the PDF.
    /// </summary>
    private const string LayoutRendererType = "NeoPrint.Layout.LayoutRenderer";

    /// <summary>Everything of ours the API process is made of (NeoPrint.Workers has no code yet).</summary>
    public static TheoryData<string> ApiAssemblies() => new()
    {
        typeof(Program).Assembly.Location,
        typeof(NeoPrint.Application.Common.ValidationError).Assembly.Location,
        typeof(ConverterTools).Assembly.Location,
        typeof(NeoPrint.Domain.Common.UserMessage).Assembly.Location,
        typeof(NeoPrint.Contracts.FileUploaded).Assembly.Location,
    };

    /// <summary>Every type an assembly refers to in another one: (assembly, full type name).</summary>
    private static List<(string Assembly, string Type)> ForeignTypes(string path)
    {
        using var stream = File.OpenRead(path);
        using var image = new PEReader(stream);
        var metadata = image.GetMetadataReader();
        var found = new List<(string, string)>();

        foreach (var handle in metadata.TypeReferences)
        {
            var type = metadata.GetTypeReference(handle);
            var scope = type.ResolutionScope;

            // A nested type's scope is the type around it.
            while (scope.Kind == HandleKind.TypeReference)
            {
                scope = metadata.GetTypeReference((TypeReferenceHandle)scope).ResolutionScope;
            }

            if (scope.Kind != HandleKind.AssemblyReference)
            {
                continue;
            }

            var assembly = metadata.GetString(metadata.GetAssemblyReference((AssemblyReferenceHandle)scope).Name);
            var @namespace = metadata.GetString(type.Namespace);

            found.Add((assembly, $"{@namespace}.{metadata.GetString(type.Name)}"));
        }

        return found;
    }

    private static bool IsParser(string assembly)
        => ParserAssemblies.Contains(assembly, StringComparer.OrdinalIgnoreCase)
           || assembly.StartsWith(PdfSharpPrefix, StringComparison.OrdinalIgnoreCase);

    [Theory]
    [MemberData(nameof(ApiAssemblies))]
    public void Nothing_in_the_api_process_can_call_a_document_or_image_parser(string assemblyPath)
    {
        var used = ForeignTypes(assemblyPath);

        used.Should().NotBeEmpty("the assembly was really read");

        used.Where(reference => IsParser(reference.Assembly))
            .Should().BeEmpty("a user's file is parsed only by the converter tool, in a process of its own");

        used.Select(reference => reference.Type)
            .Should().NotContain(LayoutRendererType, "drawing a layout decodes the user's pictures");
    }

    [Fact]
    public void The_converter_tool_is_the_assembly_that_does()
    {
        // The other half of the claim, and the check that the test above can
        // see such a reference at all. Read from disk: the tool is never
        // loaded into this process either.
        var tool = Path.Combine(AppContext.BaseDirectory, "NeoPrint.Converter.dll");

        File.Exists(tool).Should().BeTrue("the tool ships next to the API");

        var used = ForeignTypes(tool);

        used.Select(reference => reference.Assembly).Should()
            .Contain("SkiaSharp").And.Contain("NeoPrint.Pdfium").And.Contain("PdfSharp");
        used.Select(reference => reference.Type).Should().Contain(LayoutRendererType);
    }

    [Fact]
    public void Every_implementation_that_handles_file_content_goes_through_the_tool()
    {
        var infrastructure = typeof(ConverterTools).Assembly;

        Type[] contracts = [typeof(IPageRenderer), typeof(IFileEditor), typeof(ILayoutDocuments)];

        foreach (var contract in contracts)
        {
            var implementations = infrastructure.GetTypes()
                .Where(type => type is { IsClass: true, IsAbstract: false } && contract.IsAssignableFrom(type))
                .ToList();

            implementations.Should().ContainSingle($"{contract.Name} has one implementation");

            implementations[0].GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .Should().ContainSingle()
                .Which.GetParameters().Select(parameter => parameter.ParameterType)
                .Should().Contain(typeof(ConverterTools), $"{implementations[0].Name} has the converter tool do the work");
        }

        // The converters: LibreOffice and Ghostscript are programs in the
        // sandbox, and images go to the tool.
        foreach (var converter in infrastructure.GetTypes()
                     .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(IDocumentConverter).IsAssignableFrom(type)))
        {
            converter.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .Should().ContainSingle()
                .Which.GetParameters().Select(parameter => parameter.ParameterType)
                .Should().Contain(type => type == typeof(ConverterTools) || type == typeof(IProcessSandbox), converter.Name);
        }
    }
}
