using System.Text;
using System.Text.Json;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

#if IMPORT
const bool importing = true;
#else
const bool importing = false;
#endif

int exitCode = 0;
try
{
    if (args.Any(a => a is "--help" or "-h"))
        Console.WriteLine("NTAA - Not That App Again\nRun in the folder containing card_data* and/or en_* bundles.\nExtract writes Cards.json / English.csv. Import reads C*.json / *.csv.\nUse --no-pause for scripts.");
    else
    {
        if (args.Any(a => a != "--no-pause"))
            throw new InvalidDataException("Unknown option. Use --help for instructions.");
        Run(importing);
    }
}
catch (Exception ex)
{
    exitCode = 1;
    Console.Error.WriteLine("Error: " + (ex switch
    {
        InvalidDataException => ex.Message,
        UnauthorizedAccessException => "Cannot write here. Close other programs using these files, or use a writable folder.",
        IOException => "Cannot read or write a file. Close other programs using these files and check free disk space.",
        _ => "Could not read this bundle. Make sure it is a valid UnityFS bundle with its type information."
    }));
}
if (!args.Contains("--no-pause") && !Console.IsInputRedirected)
{
    Console.WriteLine("Press Enter to close.");
    Console.ReadLine();
}
return exitCode;

static void Run(bool importing)
{
    var files = Directory.GetFiles(Directory.GetCurrentDirectory());
    string? One(string label, Func<string, bool> matches)
    {
        var found = files.Where(p => matches(Path.GetFileName(p))).ToArray();
        if (found.Length > 1) throw new InvalidDataException($"Too many {label}. Keep only one in this folder.");
        return found.SingleOrDefault();
    }
    bool Starts(string name, string prefix) => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    bool Ends(string name, string suffix) => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
    var card = One("card_data bundles", n => Starts(n, "card_data"));
    var en = One("en_ bundles", n => Starts(n, "en_"));
    var json = One("C*.json files", n => Starts(n, "C") && Ends(n, ".json"));
    var csv = One("CSV files", n => Ends(n, ".csv"));
    if (card == null && en == null)
        throw new InvalidDataException("No bundles found. Put a card_data* or en_* bundle in this folder.");
    if (importing)
    {
        if (card != null && json == null) throw new InvalidDataException("Missing C*.json file for the card_data bundle.");
        if (en != null && csv == null) throw new InvalidDataException("Missing CSV file for the en_ bundle.");
        if (json != null && card == null) throw new InvalidDataException("Missing card_data bundle for the JSON file.");
        if (csv != null && en == null) throw new InvalidDataException("Missing en_ bundle for the CSV file.");
    }
    var jobs = new List<(string Bundle, string Asset, string Payload, bool Json)>();
    if (card != null) jobs.Add((card, "cards", importing ? json! : "Cards.json", true));
    if (en != null) jobs.Add((en, "LocalizedStrings", importing ? csv! : "English.csv", false));
    var staged = new List<(string Destination, string Temp)>();
    try
    {
        // Validate and stage every job before replacing any input bundle.
        foreach (var job in jobs)
        {
            if (!importing && File.Exists(job.Payload))
                throw new InvalidDataException($"{job.Payload} already exists. Move it out of this folder before extracting.");
            var manager = new AssetsManager();
            try
            {
                var bundle = manager.LoadBundleFile(job.Bundle, true);
                var (index, assets, info, field) = Find(manager, bundle, job.Asset);
                var temp = Path.Combine(Directory.GetCurrentDirectory(), ".ntaa-" + Guid.NewGuid().ToString("N"));
                staged.Add((importing ? job.Bundle : Path.GetFullPath(job.Payload), temp));
                if (!importing)
                    File.WriteAllBytes(temp, field["m_Script"].AsByteArray);
                else
                {
                    var bytes = File.ReadAllBytes(job.Payload);
                    Validate(bytes, job.Payload, job.Json);
                    field["m_Script"].AsByteArray = bytes;
                    info.SetNewData(field);
                    bundle.file.BlockAndDirInfo.DirectoryInfos[index].SetNewData(assets.file);
                    using var unpacked = new MemoryStream();
                    using (var writer = new AssetsFileWriter(unpacked))
                    {
                        bundle.file.Write(writer);
                        unpacked.Position = 0;
                        var updated = new AssetBundleFile();
                        updated.Read(new AssetsFileReader(unpacked));
                        using var packedWriter = new AssetsFileWriter(temp);
                        updated.Pack(packedWriter, AssetBundleCompressionType.LZ4, false);
                    }
                    Verify(temp, job.Asset, bytes);
                }
            }
            finally { manager.UnloadAll(); }
        }
        foreach (var item in staged)
        {
            if (importing)
                File.Replace(item.Temp, item.Destination, null);
            else
                File.Move(item.Temp, item.Destination); // Never overwrite an existing export.
            Console.WriteLine($"{(importing ? "Imported" : "Extracted")} {Path.GetFileName(item.Destination)}.");
        }
    }
    finally
    {
        foreach (var item in staged)
            if (File.Exists(item.Temp)) File.Delete(item.Temp);
    }
}

static (int Index, AssetsFileInstance Assets, AssetFileInfo Info, AssetTypeValueField Field) Find(
    AssetsManager manager, BundleFileInstance bundle, string assetName)
{
    var found = new List<(int, AssetsFileInstance, AssetFileInfo, AssetTypeValueField)>();
    for (int i = 0; i < bundle.file.BlockAndDirInfo.DirectoryInfos.Count; i++)
    {
        if (!bundle.file.IsAssetsFile(i)) continue;
        var assets = manager.LoadAssetsFileFromBundle(bundle, i, false);
        foreach (var info in assets.file.GetAssetsOfType(AssetClassID.TextAsset))
        {
            var field = manager.GetBaseField(assets, info);
            if (field["m_Name"].AsString.Equals(assetName, StringComparison.OrdinalIgnoreCase))
                found.Add((i, assets, info, field));
        }
    }
    if (found.Count != 1)
        throw new InvalidDataException(found.Count == 0
            ? $"Cannot find the {assetName} text asset in {Path.GetFileName(bundle.path)}."
            : $"Too many {assetName} text assets in {Path.GetFileName(bundle.path)}.");
    return found[0];
}

static void Validate(byte[] bytes, string path, bool json)
{
    try { new UTF8Encoding(false, true).GetString(bytes); }
    catch (DecoderFallbackException) { throw new InvalidDataException($"{Path.GetFileName(path)} must be saved as UTF-8 text."); }
    if (!json) return;
    try
    {
        ReadOnlyMemory<byte> data = bytes;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) data = data[3..];
        using var document = JsonDocument.Parse(data);
    }
    catch (JsonException) { throw new InvalidDataException($"{Path.GetFileName(path)} is not valid JSON. Fix it before importing."); }
}

static void Verify(string path, string assetName, byte[] expected)
{
    var manager = new AssetsManager();
    try
    {
        var bundle = manager.LoadBundleFile(path, true);
        var target = Find(manager, bundle, assetName);
        if (!target.Field["m_Script"].AsByteArray.AsSpan().SequenceEqual(expected))
            throw new InvalidDataException("Could not verify the edited bundle. The original has not been replaced.");
    }
    finally { manager.UnloadAll(); }
}
