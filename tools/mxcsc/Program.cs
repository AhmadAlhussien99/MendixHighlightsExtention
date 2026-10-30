// Minimal Roslyn-based compiler host, so the extension builds without a .NET SDK.
// build.ps1 compiles this file once with the C# compiler built into Windows (C# 5 syntax only), then uses it.
// Usage: mxcsc <out.dll> <refsFile> <src1.cs> [src2.cs ...]
//   refsFile: a text file with one reference assembly path per line.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

static class Program
{
    static int Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: mxcsc <out.dll> <refsFile> <src1.cs> [src2.cs ...]");
            return 2;
        }

        string outPath = args[0];
        var refs = File.ReadAllLines(args[1])
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .Select(l => (MetadataReference)MetadataReference.CreateFromFile(l))
            .ToList();

        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
        var trees = new List<SyntaxTree>();
        for (int i = 2; i < args.Length; i++)
            trees.Add(CSharpSyntaxTree.ParseText(File.ReadAllText(args[i]), parseOptions, args[i]));

        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            .WithOptimizationLevel(OptimizationLevel.Release)
            .WithNullableContextOptions(NullableContextOptions.Enable);
        var compilation = CSharpCompilation.Create(Path.GetFileNameWithoutExtension(outPath), trees, refs, options);
        var result = compilation.Emit(outPath);

        foreach (var diagnostic in result.Diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning))
            Console.WriteLine(diagnostic.ToString());
        Console.WriteLine(result.Success ? "BUILD OK" : "BUILD FAILED");
        return result.Success ? 0 : 1;
    }
}
