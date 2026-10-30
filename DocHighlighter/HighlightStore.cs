using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mendix.StudioPro.ExtensionsAPI.Services;

namespace DocHighlighter;

/// <summary>
/// Per-app highlight storage. Lives ONLY under %LOCALAPPDATA%\MendixDocHighlighter,
/// never inside the app directory, so nothing ends up in version control.
/// All members are used from the UI thread only.
/// </summary>
internal sealed class HighlightStore
{
    public static readonly string StorageRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MendixDocHighlighter");

    static readonly Dictionary<string, HighlightStore> Stores = new(StringComparer.OrdinalIgnoreCase);
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    readonly string filePath;
    readonly string appDirectory;
    readonly ILogService log;
    Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    DateTime loadedWriteTimeUtc;

    HighlightStore(string appDirectory, ILogService log)
    {
        this.appDirectory = appDirectory;
        this.log = log;
        filePath = Path.Combine(StorageRoot, FileNameFor(appDirectory));
        Load();
    }

    public static HighlightStore ForApp(string appDirectory, ILogService log)
    {
        var key = NormalizePath(appDirectory);
        lock (Stores)
        {
            if (!Stores.TryGetValue(key, out var store))
            {
                store = new HighlightStore(key, log);
                Stores[key] = store;
            }
            return store;
        }
    }

    public int Count => entries.Count;

    public string FilePath => filePath;

    public HighlightColor? Get(string unitId) =>
        entries.TryGetValue(unitId, out var entry) && Enum.TryParse<HighlightColor>(entry.Color, out var color)
            ? color
            : null;

    /// <summary>Ids of the highlighted files (items that are not model units) recorded as being in one of these modules.</summary>
    public IEnumerable<string> IdsInModules(IReadOnlySet<string> moduleIds)
    {
        foreach (var (id, entry) in entries)
            if (entry.Module != null && moduleIds.Contains(entry.Module))
                yield return id;
    }

    /// <summary>
    /// Sets or (with <paramref name="color"/> null) removes the highlight of one unit. <paramref name="fileModule"/> is
    /// the module of an item that is a file rather than a model unit, so module counts can include it.
    /// </summary>
    public void Set(string unitId, string unitName, HighlightColor? color, string? fileModule = null)
    {
        // Start from the latest file so a change made by another Studio Pro instance is not overwritten.
        ReloadIfChanged();
        if (color is null)
        {
            if (!entries.Remove(unitId)) return;
        }
        else
        {
            entries[unitId] = new Entry { Color = color.Value.ToString(), Name = unitName, Module = fileModule };
        }
        Save();
    }

    public void Remove(IReadOnlySet<string> unitIds)
    {
        ReloadIfChanged();
        var removed = false;
        foreach (var id in new List<string>(entries.Keys))
            if (unitIds.Contains(id)) removed |= entries.Remove(id);
        if (removed) Save();
    }

    public void Clear()
    {
        ReloadIfChanged();
        if (entries.Count == 0) return;
        entries.Clear();
        Save();
    }

    /// <summary>Picks up edits made by another Studio Pro instance that has the same app open.</summary>
    public void ReloadIfChanged()
    {
        var writeTime = File.Exists(filePath) ? File.GetLastWriteTimeUtc(filePath) : DateTime.MinValue;
        if (writeTime != loadedWriteTimeUtc) Load();
    }

    void Load()
    {
        entries = new(StringComparer.OrdinalIgnoreCase);
        loadedWriteTimeUtc = DateTime.MinValue;
        if (!File.Exists(filePath)) return;

        loadedWriteTimeUtc = File.GetLastWriteTimeUtc(filePath);
        try
        {
            var file = JsonSerializer.Deserialize<StoreFile>(File.ReadAllText(filePath));
            if (file?.Highlights != null)
                entries = new(file.Highlights, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            // A corrupt file must never break Studio Pro. Keep a copy before the next save replaces it.
            var backupPath = Path.ChangeExtension(filePath, $".corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            try
            {
                File.Copy(filePath, backupPath, overwrite: true);
            }
            catch (Exception)
            {
                backupPath = "<backup failed>";
            }
            log.Error($"DocHighlighter: could not read {filePath}; starting empty. A copy was saved as {backupPath}", ex);
        }
    }

    void Save()
    {
        Directory.CreateDirectory(StorageRoot);
        var file = new StoreFile { AppDirectory = appDirectory, Highlights = entries };
        var tempPath = filePath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(file, JsonOptions));
        File.Move(tempPath, filePath, overwrite: true);
        loadedWriteTimeUtc = File.GetLastWriteTimeUtc(filePath);
    }

    static string NormalizePath(string path) => Path.GetFullPath(path).TrimEnd('\\', '/');

    /// <summary>Readable, collision-free file name: the app folder name plus a hash of its full path.</summary>
    static string FileNameFor(string appDirectory)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(appDirectory.ToLowerInvariant())))[..12];
        var name = new StringBuilder();
        foreach (var c in Path.GetFileName(appDirectory))
            name.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        return $"{name}_{hash}.json";
    }

    sealed class StoreFile
    {
        public string? AppDirectory { get; set; }
        public Dictionary<string, Entry>? Highlights { get; set; }
    }

    sealed class Entry
    {
        public string Color { get; set; } = "";
        public string Name { get; set; } = "";
        /// <summary>Only for files (not model units): the id of the module the item is shown under.</summary>
        public string? Module { get; set; }
    }
}
