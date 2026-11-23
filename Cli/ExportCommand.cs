using System.Text;
using System.Text.Json;
using RpfPreview.Core;

namespace RpfPreview.Cli;

public static class ExportCommand
{
    public const string Usage = """
        rpf9-cli export <source>... --out <folder> [options]

        Exports matching assets. Models become OBJ + MTL with PNG color textures, textures become PNG,
        and anything else is extracted as stored. A manifest.json describes what was written.

          -o, --out <folder>       Output folder (required)
          -q, --query <text>       Search text, same syntax as the app
          -t, --type <type>        Asset type to export (default: models)
              --id <id>            Only the asset with this id (from 'search'); repeat or separate with commas
              --available          Skip assets the current decoders cannot extract
              --best               Keep only the largest variant of each name
              --merge <name>       Write all models into one <name>.obj, keeping each part as its own object
              --lod <0-3>          Level of detail, 0 is the most detailed (default: 0)
              --max-texture <px>   Largest texture side in pixels (default: 2048)
              --no-textures        Write geometry and UVs only
              --overwrite          Replace files that already exist
              --decoder <file>     Local RPF9 decoder configuration (default: the app's configuration)
              --quiet              Hide progress messages
        """;

    private static readonly OptionSpec[] Options =
    [
        .. AssetSelection.Options, .. CommonOptions.All,
        new("out", "o"),
        new("merge"),
        new("lod"),
        new("max-texture"),
        new("no-textures", TakesValue: false),
        new("overwrite", TakesValue: false)
    ];

    private sealed record Settings(string Output, string? Merge, int Lod, int MaxTexture, bool Textures, bool Overwrite);
    private sealed record TextureRecord(int Shader, string? File, string Detail);
    private sealed record PartRecord(string Id, string Name, string Source, string? Obj, int Vertices, int Triangles, IReadOnlyList<TextureRecord> Textures);
    private sealed record FileRecord(string Id, string Name, string Source, string File);
    private sealed record Failure(string Id, string Name, string Reason);
    private sealed record Manifest(string Tool, int Lod, string? Merged, IReadOnlyList<PartRecord> Models, IReadOnlyList<FileRecord> Images, int Extracted, IReadOnlyList<Failure> Failures);

    public static async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken token)
    {
        var line = CommandLine.Parse(args, Options);
        var settings = ReadSettings(line);
        if (!settings.Overwrite && File.Exists(Path.Combine(settings.Output, "manifest.json")))
            throw new UsageException($"{settings.Output} already holds an export; choose a new folder or add --overwrite.");
        var reporter = CommonOptions.Reporter(line);
        var selection = AssetSelection.From(line, "models");
        var workspace = SourceWorkspace.Open(line.Positionals, line.Value("decoder"), reporter, token);
        var assets = selection.Apply(workspace.Index);
        if (assets.Count == 0)
        {
            reporter.Info("No assets match.");
            return ExitCodes.NoMatches;
        }
        Directory.CreateDirectory(LocalPath.Require(settings.Output));
        var exporter = new Exporter(workspace, settings, reporter, token);
        var models = assets.Where(a => a.Kind == "model").ToArray();
        var images = assets.Where(a => a.IsImage).ToArray();
        var others = assets.Except(models).Except(images).ToArray();
        var parts = exporter.ExportModels(models);
        var imageFiles = exporter.ExportImages(images);
        int extracted = await exporter.ExtractAsync(others);
        var manifest = new Manifest("rpf9-cli", settings.Lod, settings.Merge is null ? null : settings.Merge + ".obj", parts, imageFiles, extracted, exporter.Failures);
        exporter.WriteText("manifest.json", JsonSerializer.Serialize(manifest, CliJson.Options));
        reporter.ClearProgress();
        int textured = parts.Sum(p => p.Textures.Count(t => t.File is not null));
        int missing = parts.Sum(p => p.Textures.Count(t => t.File is null));
        reporter.Info($"Exported {parts.Count:N0} models ({textured:N0} textured materials, {missing:N0} without texture), {imageFiles.Count:N0} images and {extracted:N0} other files to {settings.Output}");
        foreach (var failure in exporter.Failures.Take(10)) reporter.Warn($"{failure.Name} [{failure.Id}]: {failure.Reason}");
        if (exporter.Failures.Count > 10) reporter.Warn($"{exporter.Failures.Count - 10:N0} more failures are listed in manifest.json.");
        return parts.Count + imageFiles.Count + extracted == 0 ? ExitCodes.Failure : ExitCodes.Success;
    }

    private static Settings ReadSettings(CommandLine line)
    {
        string output = line.Value("out") ?? throw new UsageException("Choose an output folder with --out.");
        string? merge = line.Value("merge");
        if (merge is not null && (merge.Length == 0 || merge.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new UsageException("--merge needs a plain file name without folders.");
        return new(Path.GetFullPath(output), merge is null ? null : Path.GetFileNameWithoutExtension(merge),
            line.Integer("lod", 0, 0, RageModelDecoder.LodCount - 1),
            line.Integer("max-texture", 2048, 16, 16384),
            !line.Has("no-textures"), line.Has("overwrite"));
    }

    private sealed class Exporter(SourceWorkspace workspace, Settings settings, Reporter reporter, CancellationToken token)
    {
        private readonly ModelTextureResolver _resolver = new(workspace.Index, settings.MaxTexture);
        private readonly Dictionary<ulong, ModelTextureLookup> _lookups = [];
        private readonly Dictionary<(ulong Key, bool Opaque), string> _textureFiles = [];
        private readonly HashSet<string> _usedNames = new(StringComparer.OrdinalIgnoreCase);
        private (string Key, byte[] Data)? _resource;

        public List<Failure> Failures { get; } = [];

        public IReadOnlyList<PartRecord> ExportModels(IReadOnlyList<WorkspaceAsset> assets)
        {
            var records = new List<PartRecord>();
            var merged = new List<ObjModelPart>();
            for (int i = 0; i < assets.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var asset = assets[i];
                string name = AssetLabels.Name(asset), id = AssetLabels.Id(asset);
                reporter.Progress($"Model {i + 1}/{assets.Count}: {name}");
                if (!asset.Available) { Failures.Add(new(id, name, asset.Entry.Limitation ?? "Not extractable with the current decoders.")); continue; }
                try
                {
                    byte[] resource = ReadResource(asset);
                    int split = SystemBytes(asset.Entry);
                    var model = RageModelDecoder.Decode(resource.AsSpan(0, split), resource.AsSpan(split), asset.Entry.ResourceDrawableOffset, settings.Lod);
                    if (model is null) { Failures.Add(new(id, name, $"LOD {settings.Lod} could not be decoded.")); continue; }
                    var textures = settings.Textures ? ResolveTextures(new(asset.Document, model, resource, split)) : [];
                    var maps = textures.Where(t => t.File is not null).ToDictionary(t => t.Shader, t => t.File!);
                    string partName = UniqueName(AssetLabels.SafeFileName(name), id);
                    var part = new ObjModelPart(partName, model, maps);
                    string? obj = null;
                    if (settings.Merge is null)
                    {
                        obj = partName + ".obj";
                        WriteObj(partName, [part]);
                    }
                    else merged.Add(part);
                    records.Add(new(id, name, asset.SourceName, obj, model.Meshes.Sum(m => m.Positions.Count), model.Meshes.Sum(m => m.Indices.Count / 3), textures));
                }
                catch (Exception ex) when (IsExportFailure(ex)) { Failures.Add(new(id, name, ex.Message)); }
            }
            if (settings.Merge is not null && merged.Count > 0) WriteObj(settings.Merge, merged);
            return records;
        }

        public IReadOnlyList<FileRecord> ExportImages(IReadOnlyList<WorkspaceAsset> assets)
        {
            var records = new List<FileRecord>();
            for (int i = 0; i < assets.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var asset = assets[i];
                string name = AssetLabels.Name(asset), id = AssetLabels.Id(asset);
                reporter.Progress($"Image {i + 1}/{assets.Count}: {name}");
                try
                {
                    var pixels = DecodeImage(asset);
                    if (pixels is null) { Failures.Add(new(id, name, "Not a decodable texture.")); continue; }
                    string file = Path.Combine("images", UniqueName(AssetLabels.SafeFileName(name), id) + ".png");
                    WriteBytes(file, PngWriter.Encode(pixels));
                    records.Add(new(id, name, asset.SourceName, file.Replace('\\', '/')));
                }
                catch (Exception ex) when (IsExportFailure(ex)) { Failures.Add(new(id, name, ex.Message)); }
            }
            return records;
        }

        public async Task<int> ExtractAsync(IReadOnlyList<WorkspaceAsset> assets)
        {
            var available = assets.Where(a => a.Available).ToArray();
            foreach (var asset in assets.Except(available))
                Failures.Add(new(AssetLabels.Id(asset), AssetLabels.Name(asset), asset.Entry.Limitation ?? "Not extractable with the current decoders."));
            if (available.Length == 0) return 0;
            var progress = new Progress<ExtractionProgress>(p => reporter.Progress($"Extracting {p.CompletedFiles}/{p.TotalFiles}: {p.CurrentFile}"));
            try
            {
                await WorkspaceAssetExporter.ExtractAsync(available, Path.Combine(settings.Output, "files"), progress, token);
                return available.Length;
            }
            catch (Exception ex) when (IsExportFailure(ex))
            {
                Failures.Add(new("", "extraction", ex.Message));
                return 0;
            }
        }

        public void WriteText(string relative, string content) => WriteBytes(relative, new UTF8Encoding(false).GetBytes(content));

        private List<TextureRecord> ResolveTextures(ModelTextureSource source)
        {
            var records = new List<TextureRecord>();
            var textured = ModelTextureResolver.TexturedShaderIndices(source.Model);
            foreach (var material in (source.Model.Materials ?? []).Where(m => textured.Contains(m.ShaderIndex)))
            {
                var candidates = ModelTextureBindings.ColorTextures(material);
                if (candidates.Count == 0) { records.Add(new(material.ShaderIndex, null, "No recognized color sampler.")); continue; }
                bool opaque = ModelTextureResolver.IsOpaque(material);
                var notes = new List<string>();
                TextureRecord? found = null;
                foreach (var candidate in candidates)
                {
                    ulong key = candidate.Reference.OpaqueId64;
                    if (_textureFiles.TryGetValue((key, opaque), out var existing)) { found = new(material.ShaderIndex, existing, _lookups[key].Detail); break; }
                    if (!_lookups.TryGetValue(key, out var lookup)) _lookups[key] = lookup = _resolver.Find(source, candidate.Reference, token);
                    if (!lookup.Found) { notes.Add(candidate.ParameterName + ": " + lookup.Detail); continue; }
                    string file = $"textures/{key:X16}{(opaque ? "_opaque" : "")}.png";
                    WriteBytes(file, PngWriter.Encode(opaque ? Opaque(lookup.Pixels!) : lookup.Pixels!));
                    _textureFiles[(key, opaque)] = file;
                    found = new(material.ShaderIndex, file, lookup.Detail);
                    break;
                }
                records.Add(found ?? new(material.ShaderIndex, null, string.Join("; ", notes.Distinct().Take(4))));
            }
            return records;
        }

        private RageTexturePixels? DecodeImage(WorkspaceAsset asset)
        {
            if (asset.PoolTexture is { } pool) return TtpfReader.DecodeTexturePreview(asset.SourcePath, pool.Index, settings.MaxTexture, token);
            if (asset.Texture is not { } texture) return null;
            byte[] resource = ReadResource(asset);
            int split = SystemBytes(asset.Entry), mip = 0;
            while (mip + 1 < texture.MipCount && (Math.Max(1, texture.Width >> mip) > settings.MaxTexture || Math.Max(1, texture.Height >> mip) > settings.MaxTexture)) mip++;
            return RageTextureDecoder.DecodeTexturePixelsAtMip(resource.AsSpan(0, split), resource.AsSpan(split), texture.Index, mip);
        }

        private void WriteObj(string baseName, IReadOnlyList<ObjModelPart> parts)
        {
            var document = ModelObjWriter.Write(parts, baseName + ".mtl");
            WriteText(baseName + ".obj", document.Obj);
            WriteText(baseName + ".mtl", document.Mtl);
        }

        private void WriteBytes(string relative, byte[] content)
        {
            string path = ArchiveReader.SafeDestination(settings.Output, relative.Replace('\\', '/'));
            string directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(LocalPath.Require(directory));
            if (File.Exists(path) && !settings.Overwrite) throw new IOException($"{relative} already exists; use --overwrite to replace it.");
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".partial";
            try
            {
                File.WriteAllBytes(temporary, content);
                File.Move(temporary, path, settings.Overwrite);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        private byte[] ReadResource(WorkspaceAsset asset)
        {
            string key = asset.SourcePath + ":" + asset.Entry.Offset;
            if (_resource?.Key != key) _resource = (key, ArchiveReader.ReadResourceData(asset.Document, asset.Entry, token));
            return _resource.Value.Data;
        }

        private string UniqueName(string name, string id)
        {
            if (_usedNames.Add(name)) return name;
            string withId = name + "_" + id.Replace(':', '_');
            for (int n = 2; !_usedNames.Add(withId); n++) withId = $"{name}_{id.Replace(':', '_')}_{n}";
            return withId;
        }

        private static int SystemBytes(ArchiveEntry entry) => entry.ResourceSystemBytes ?? checked((int)entry.Segments![0].Size);

        private static RageTexturePixels Opaque(RageTexturePixels pixels)
        {
            byte[] copy = (byte[])pixels.Bgra32.Clone();
            for (int i = 3; i < copy.Length; i += 4) copy[i] = 255;
            return pixels with { Bgra32 = copy };
        }

        private static bool IsExportFailure(Exception ex)
            => ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException or ArgumentException or OverflowException;
    }
}
