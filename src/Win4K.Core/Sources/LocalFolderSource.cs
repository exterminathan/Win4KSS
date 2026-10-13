namespace Win4K.Core;

/// <summary>
/// Plays video files from a folder on disk.
///
/// This exists so the screen saver is not wholly dependent on Apple continuing to host the
/// aerials — the failure that eventually made cdima/aerial useless. Local clips are already
/// on disk, so they bypass the download cache entirely.
/// </summary>
public sealed class LocalFolderSource(string folder, bool recursive = true) : IClipInfoSource
{
    public const string SourceIdPrefix = "local:";

    private static readonly string[] Extensions =
        [".mp4", ".mkv", ".mov", ".webm", ".m4v", ".avi", ".wmv", ".ts"];

    public string Folder { get; } = folder;

    public string Id => SourceIdPrefix + Folder;

    public string DisplayName => $"Folder: {Folder}";

    public async Task<IReadOnlyList<Clip>> GetClipsAsync(CancellationToken ct = default)
    {
        var infos = await GetClipInfosAsync(ct).ConfigureAwait(false);
        return infos.Select(i => i.ToClip(VideoQuality.Auto)).OfType<Clip>().ToList();
    }

    public Task<IReadOnlyList<ClipInfo>> GetClipInfosAsync(CancellationToken ct = default)
    {
        var clips = new List<ClipInfo>();

        if (string.IsNullOrWhiteSpace(Folder) || !Directory.Exists(Folder))
        {
            Log.Warn($"Local source: folder not found: {Folder}");
            return Task.FromResult<IReadOnlyList<ClipInfo>>(clips);
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };

        foreach (var path in Directory.EnumerateFiles(Folder, "*", options))
        {
            ct.ThrowIfCancellationRequested();

            var extension = Path.GetExtension(path);
            if (!Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                continue;

            long? size = null;
            try
            {
                size = new FileInfo(path).Length;
            }
            catch
            {
                // A file we cannot stat is still probably playable; carry on without the size.
            }

            var title = Path.GetFileNameWithoutExtension(path);
            var info = new ClipInfo
            {
                Key = Catalogs.ClipKey.ForLocal(path),
                SourceId = "local",
                SourceIds = ["local"],
                CatalogId = Id,
                Title = title,
                Category = "Local",
                TimeOfDay = ClipHeuristics.FromLabel(title),
                SizeBytes = size,
                LocalPath = path,
            };
            clips.Add(info with { Tags = Catalogs.TagBuilder.Build(info) });
        }

        Log.Info($"Local source: {clips.Count} clips in {Folder}");
        return Task.FromResult<IReadOnlyList<ClipInfo>>(clips);
    }
}
