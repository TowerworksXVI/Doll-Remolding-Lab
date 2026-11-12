// Generates a string catalog's accessor class from its neutral resx: one property per entry without
// holes, one method per entry with holes, each reading through Remold.Core.Text.Catalog. The output is
// committed, so it is deterministic: members sorted by key, LF line endings, no timestamps.
//
//   usage: dotnet run --project tools/GenStrings -- <resx> <output .cs> <namespace> <class> <resource base name>
//
// Every entry must carry a comment in the catalog's shape with every hole described; any entry that does
// not stops the run before the output is touched, naming the key.

using System.Text;
using Remold.Tools.GenStrings;

if (args.Length != 5)
{
    Console.Error.WriteLine(
        "usage: GenStrings <resx> <output .cs> <namespace> <class> <resource base name>");
    return 1;
}
string resxPath = Path.GetFullPath(args[0]);
string outputPath = Path.GetFullPath(args[1]);
string ns = args[2];
string className = args[3];
string baseName = args[4];

var (entries, problems) = ResxCatalog.Read(resxPath, className);
if (problems.Count > 0)
{
    foreach (string problem in problems) Console.Error.WriteLine($"{Path.GetFileName(resxPath)}: {problem}");
    return 1;
}

// The source named in the header is the resx's folder and file name, so the output does not depend on
// where the tool was run from.
string source = Path.GetFileName(Path.GetDirectoryName(resxPath)) + "/" + Path.GetFileName(resxPath);
string code = ResxCatalog.Generate(entries, source, ns, className, baseName);

byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(code);
if (File.Exists(outputPath) && File.ReadAllBytes(outputPath).AsSpan().SequenceEqual(bytes))
{
    Console.WriteLine($"{outputPath} is up to date ({entries.Count} entries).");
    return 0;
}
File.WriteAllBytes(outputPath, bytes);
Console.WriteLine($"Wrote {outputPath} ({entries.Count} entries).");
return 0;
