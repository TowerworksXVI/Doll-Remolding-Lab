// Packs a win-x64 publish folder into the release layout: the exe alone at the root — the one thing
// a user must find — with every assembly and runtime file under app\. The root exe is a fresh apphost
// whose embedded app path points into app\, so the runtime resolves everything from there while the
// modder-facing folders (mods, settings) are created beside the exe at the root.
//
//   usage: dotnet run --project tools/PackRelease -- <publishDir> <outDir> [exeName]
//
// The publish folder itself is left untouched; its own flat exe is simply not carried over.

using Microsoft.NET.HostModel.AppHost;

const string AppDll = "Remold.App.dll";

if (args.Length is < 2 or > 3)
{
    Console.WriteLine("usage: PackRelease <publishDir> <outDir> [exeName]");
    return 1;
}
string pubDir = Path.GetFullPath(args[0]);
string outDir = Path.GetFullPath(args[1]);
string exeName = args.Length > 2 ? args[2] : "Doll Remolding Lab.exe";

if (!File.Exists(Path.Combine(pubDir, AppDll)))
{
    Console.WriteLine($"{pubDir} holds no {AppDll} — point at the win-x64 publish output.");
    return 1;
}
// The shipped sharing seed has to end up beside the assemblies: without it a fresh install pays the full
// sharing crawl on first launch. It is a build Content item, so a missing one means the publish folder is
// incomplete — checked HERE, beside the other input guard and before anything is written, so a refusal
// leaves outDir empty and the retry sees this message rather than "pack into a fresh folder".
const string Seed = @"data\sharing_seed.json";
if (!File.Exists(Path.Combine(pubDir, Seed)))
{
    Console.WriteLine($"{pubDir} holds no {Seed} — re-publish the app.");
    return 1;
}
if (Directory.Exists(outDir) && Directory.EnumerateFileSystemEntries(outDir).Any())
{
    Console.WriteLine($"{outDir} exists and is not empty — pack into a fresh folder.");
    return 1;
}

// every publish file except the flat apphost goes under app\
string appDir = Path.Combine(outDir, "app");
Directory.CreateDirectory(appDir);
string flatExe = Path.ChangeExtension(AppDll, ".exe");
int copied = 0;
foreach (var src in Directory.EnumerateFiles(pubDir, "*", SearchOption.AllDirectories))
{
    var rel = Path.GetRelativePath(pubDir, src);
    if (string.Equals(rel, flatExe, StringComparison.OrdinalIgnoreCase)) continue;
    var dst = Path.Combine(appDir, rel);
    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
    File.Copy(src, dst);
    copied++;
}

// the newest installed apphost template on the app's own major version — host/runtime compatibility
// is per major, and the publish is self-contained, so the template only has to speak the protocol
string packRoot = Path.Combine(
    Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? @"C:\Program Files\dotnet",
    "packs", "Microsoft.NETCore.App.Host.win-x64");
int major = Environment.Version.Major;
var template = Directory.EnumerateDirectories(packRoot)
    .Select(d => (Dir: d, Ok: Version.TryParse(Path.GetFileName(d), out var v), Ver: v))
    .Where(x => x.Ok && x.Ver!.Major == major)
    .OrderByDescending(x => x.Ver)
    .Select(x => Path.Combine(x.Dir, "runtimes", "win-x64", "native", "apphost.exe"))
    .FirstOrDefault(File.Exists);
if (template is null)
{
    Console.WriteLine($"no apphost template for major {major} under {packRoot} — install the matching SDK.");
    return 1;
}

HostWriter.CreateAppHost(
    appHostSourceFilePath: template,
    appHostDestinationFilePath: Path.Combine(outDir, exeName),
    appBinaryFilePath: Path.Combine("app", AppDll),
    windowsGraphicalUserInterface: true,
    assemblyToCopyResorcesFrom: Path.Combine(appDir, AppDll));

Console.WriteLine($"packed: {outDir}");
Console.WriteLine($"  {exeName}  (apphost -> app\\{AppDll})");
Console.WriteLine($"  app\\  ({copied} files)");
return 0;
