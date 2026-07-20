using System.Globalization;
using System.Text.Json;
using Soundstage.Core.Apo;

// AutoEqPacker — development-time tool.
//
// Packs the AutoEq results database (github.com/jaakkopasanen/AutoEq, MIT) into the
// compact JSON catalog embedded in Soundstage.Core. Expects a sparse checkout containing
// only the ParametricEQ.txt files, e.g.:
//
//   git clone --depth 1 --filter=blob:none --sparse https://github.com/jaakkopasanen/AutoEq.git
//   git -C AutoEq sparse-checkout set --no-cone "results/**/*ParametricEQ.txt"
//
// Usage: AutoEqPacker --repo <path-to-AutoEq-clone> --out <autoeq.min.json>

string? repoPath = null;
string? outPath = null;
for (var i = 0; i < args.Length - 1; i++)
{
    switch (args[i])
    {
        case "--repo":
            repoPath = args[i + 1];
            break;
        case "--out":
            outPath = args[i + 1];
            break;
    }
}

if (repoPath is null || outPath is null)
{
    Console.Error.WriteLine("Usage: AutoEqPacker --repo <path-to-AutoEq-clone> --out <autoeq.min.json>");
    return 1;
}

var resultsDir = Path.Combine(repoPath, "results");
if (!Directory.Exists(resultsDir))
{
    Console.Error.WriteLine($"No results directory at {resultsDir}");
    return 1;
}

var files = Directory.EnumerateFiles(resultsDir, "*ParametricEQ.txt", SearchOption.AllDirectories)
    .OrderBy(f => f, StringComparer.Ordinal)
    .ToList();
Console.WriteLine($"Found {files.Count} ParametricEQ files.");

var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
var entries = 0;
var skipped = 0;

await using var output = File.Create(outPath);
using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = false });

writer.WriteStartObject();
writer.WriteNumber("v", 1);
writer.WriteString("source", "AutoEq — github.com/jaakkopasanen/AutoEq");
writer.WriteString("license", "MIT");
writer.WriteStartArray("entries");

foreach (var file in files)
{
    var relative = Path.GetRelativePath(resultsDir, file).Replace('\\', '/');
    var source = relative.Split('/')[0];
    var fileName = Path.GetFileName(file);
    var model = fileName[..fileName.LastIndexOf("ParametricEQ.txt", StringComparison.Ordinal)].Trim();
    if (model.Length == 0)
    {
        model = Path.GetFileName(Path.GetDirectoryName(file)) ?? fileName;
    }

    if (!seen.Add($"{model}|{source}"))
    {
        skipped++;
        continue;
    }

    var parsed = ApoConfigParser.Parse(File.ReadAllText(file));
    double preamp = 0;
    var filters = new List<FilterCommand>();
    foreach (var command in parsed.Document.Commands)
    {
        switch (command)
        {
            case PreampCommand p:
                preamp = p.Db;
                break;
            case FilterCommand f when f.Enabled:
                filters.Add(f);
                break;
        }
    }

    if (filters.Count == 0)
    {
        skipped++;
        continue;
    }

    writer.WriteStartObject();
    writer.WriteString("n", model);
    writer.WriteString("s", source);
    writer.WriteNumber("p", Math.Round(preamp, 2));
    writer.WriteStartArray("f");
    foreach (var f in filters)
    {
        writer.WriteStartArray();
        writer.WriteStringValue(f.Type.ToApoCode());
        writer.WriteNumberValue(Math.Round(f.FrequencyHz, 1));
        writer.WriteNumberValue(Math.Round(f.GainDb, 2));
        writer.WriteNumberValue(Math.Round(f.Q ?? 1.0, 3));
        writer.WriteEndArray();
    }

    writer.WriteEndArray();
    writer.WriteEndObject();
    entries++;
}

writer.WriteEndArray();
writer.WriteEndObject();
writer.Flush();

Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
    $"Packed {entries} entries ({skipped} skipped) → {outPath} ({new FileInfo(outPath).Length / 1024.0 / 1024.0:0.00} MB)"));
return 0;
