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
    bool readFailureLogged;

    /// <summary>Goes up whenever the highlights change (here or in another Studio Pro instance), so results computed
    /// from them can be cached until then.</summary>
    public int Version { get; private set; }

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

    // Enum.TryParse also accepts numbers ("7") and names it does not know as numbers, so check the value is a real color.
    public HighlightColor? Get(string unitId) =>
        entries.TryGetValue(unitId, out var entry) && Enum.TryParse<HighlightColor>(entry.Color, out var color) && Enum.IsDefined(color)
            ? color
            : null;

    /// <summary>Every highlighted item, with the module recorded for items that are files rather than model units.</summary>
    public IEnumerable<(string Id, string? FileModule)> Highlighted()
    {
        foreach (var (id, entry) in entries)
            if (Get(id) != null)
                yield return (id, entry.Module);
    }

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
        EnsureCurrent();
        var previous = Snapshot();
        if (color is null)
        {
            if (!entries.Remove(unitId)) return;
        }
        else
        {
            entries[unitId] = new Entry { Color = color.Value.ToString(), Name = unitName, Module = fileModule };
        }
        SaveOrRollBack(previous);
    }

    public void Remove(IReadOnlySet<string> unitIds)
    {
        EnsureCurrent();
        var previous = Snapshot();
        var removed = false;
        foreach (var id in new List<string>(entries.Keys))
            if (unitIds.Contains(id)) removed |= entries.Remove(id);
        if (removed) SaveOrRollBack(previous);
    }

    /// <summary>
    /// Picks up edits made by another Studio Pro instance that has the same app open. Returns false when the file
    /// changed but could not be read right now; the highlights shown are then kept and it is tried again next time.
    /// </summary>
    public bool ReloadIfChanged()
    {
        var writeTime = File.Exists(filePath) ? File.GetLastWriteTimeUtc(filePath) : DateTime.MinValue;
        return writeTime == loadedWriteTimeUtc || Load();
    }

    /// <summary>
    /// Start every change from the latest file, so a change made by another Studio Pro instance is not overwritten.
    /// If that file cannot be read, saving would replace every highlight in it, so the change is refused instead.
    /// </summary>
    void EnsureCurrent()
    {
        if (!ReloadIfChanged())
            throw new IOException($"{filePath} is in use and could not be read; the change was not saved. Try again.");
    }

    bool Load()
    {
        if (!File.Exists(filePath))
        {
            Replace(new(StringComparer.OrdinalIgnoreCase), DateTime.MinValue);
            return true;
        }

        var writeTime = File.GetLastWriteTimeUtc(filePath);
        string json;
        try
        {
            json = File.ReadAllText(filePath);
            readFailureLogged = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Usually brief: antivirus, a backup or sync tool, or another Studio Pro instance replacing the file.
            // This is not corruption, so keep the highlights shown and leave the load time alone to retry next time.
            if (!readFailureLogged)
            {
                readFailureLogged = true;
                log.Warn($"DocHighlighter: could not read {filePath} right now ({ex.Message}); will retry.");
            }
            return false;
        }

        var loaded = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var file = JsonSerializer.Deserialize<StoreFile>(json);
            if (file?.Highlights != null)
                foreach (var (id, entry) in file.Highlights)
                    // A hand-edited file can hold "id": null; such an entry means nothing and would break every lookup.
                    if (entry != null)
                        loaded[id] = entry;
        }
        catch (JsonException ex)
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
        Replace(loaded, writeTime);
        return true;
    }

    void Replace(Dictionary<string, Entry> newEntries, DateTime writeTimeUtc)
    {
        entries = newEntries;
        loadedWriteTimeUtc = writeTimeUtc;
        Version++;
    }

    Dictionary<string, Entry> Snapshot() => new(entries, StringComparer.OrdinalIgnoreCase);

    /// <summary>Saves, or if that fails puts the highlights back as they were, so the screen never shows a change
    /// that is not on disk and would be gone after a restart. The failure is passed on to be logged.</summary>
    void SaveOrRollBack(Dictionary<string, Entry> previous)
    {
        try
        {
            Save();
        }
        catch (Exception)
        {
            entries = previous;
            Version++;
            throw;
        }
    }

    void Save()
    {
        Directory.CreateDirectory(StorageRoot);
        var file = new StoreFile { AppDirectory = appDirectory, Highlights = entries };
        var tempPath = filePath + ".tmp";
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(file, JsonOptions));
            File.Move(tempPath, filePath, overwrite: true);
        }
        catch (Exception)
        {
            try
            {
                File.Delete(tempPath);
            }
            catch (Exception)
            {
                // Best effort; the next save overwrites it anyway.
            }
            throw;
        }
        loadedWriteTimeUtc = File.GetLastWriteTimeUtc(filePath);
        Version++;
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
