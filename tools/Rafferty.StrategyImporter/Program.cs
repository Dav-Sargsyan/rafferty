using System.Text.Json;
using System.Text.RegularExpressions;
using Rafferty.Core;
using Rafferty.Shared;

if (args.Length is < 1 or > 3)
{
    Console.Error.WriteLine("Usage: Rafferty.StrategyImporter <input.bat|reference-dir> [output.json] [source-revision]");
    return 2;
}

var input = Path.GetFullPath(args[0]);
if (Directory.Exists(input))
{
    var output = args.Length >= 2 ? Path.GetFullPath(args[1]) : Path.Combine(input, "strategies.json");
    var revision = args.Length == 3 ? args[2] : "unknown";
    var files = Directory.GetFiles(input, "general*.bat", SearchOption.TopDirectoryOnly)
        .OrderBy(path => NaturalSortKey(Path.GetFileName(path)), StringComparer.OrdinalIgnoreCase)
        .ToArray();
    if (files.Length == 0) throw new InvalidDataException("The reference directory contains no general*.bat strategies.");

    var strategies = new List<Strategy>();
    foreach (var file in files)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        strategies.Add(BatStrategyImporter.Import(Slug(name), name, await File.ReadAllTextAsync(file),
            $"Upstream strategy retained from revision {revision}; MIT notices are bundled."));
    }

    var database = new StrategyDatabase(1, $"Upstream reference revision {revision}", DateTimeOffset.UtcNow, strategies);
    await File.WriteAllTextAsync(output, JsonSerializer.Serialize(database, JsonDefaults.Options));
    Console.WriteLine($"Imported {strategies.Count} complete strategies to {output}");
    return 0;
}

if (!File.Exists(input)) throw new FileNotFoundException("Input BAT was not found.", input);
var singleOutput = args.Length >= 2 ? Path.GetFullPath(args[1]) : Path.ChangeExtension(input, ".strategy.json");
var singleName = Path.GetFileNameWithoutExtension(input);
var single = BatStrategyImporter.Import(Slug(singleName), singleName, await File.ReadAllTextAsync(input),
    "Upstream strategy retained under its bundled MIT notice.");
await File.WriteAllTextAsync(singleOutput, JsonSerializer.Serialize(single, JsonDefaults.Options));
Console.WriteLine(singleOutput);
return 0;

static string Slug(string name) => new string(name.ToLowerInvariant()
    .Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-')
    .ToArray()).Trim('-');

static string NaturalSortKey(string value) => Regex.Replace(value, @"\d+", match => match.Value.PadLeft(8, '0'));
