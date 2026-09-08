using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Timers;
using Voltage.Editor.DebugUtils;
using Voltage.Editor.ProjectFile;
using Voltage.Editor.Utils;

namespace Voltage.Editor.Assets
{
    /// <summary>
    /// Serialized body of a <c>.meta</c> sidecar file.
    /// Kept intentionally minimal: only the stable GUID is required.
    /// Additional fields (import settings, etc.) can be appended later without
    /// breaking existing sidecars.
    /// </summary>
    internal sealed class AssetMetaData
    {
        /// <summary>Stable, per-file GUID. Written once; never regenerated.</summary>
        public string Guid = string.Empty;
    }

    /// <summary>
    /// A single discovered asset file.
    /// </summary>
    public sealed class AssetItem
    {
        /// <summary>Absolute path to the file on disk.</summary>
        public string AbsolutePath { get; }

        /// <summary>File name including extension (e.g. "Player.png").</summary>
        public string FileName { get; }

        /// <summary>Extension lower-cased with leading dot (e.g. ".png").</summary>
        public string Extension { get; }

        /// <summary>Label of the source folder group this asset belongs to (e.g. "Content").</summary>
        public string FolderLabel { get; }

        /// <summary>Resolved type descriptor from <see cref="AssetTypeRegistry"/>.</summary>
        public AssetTypeDescriptor Descriptor { get; }

        public AssetItem(string absolutePath, string folderLabel)
        {
            AbsolutePath = absolutePath;
            FileName     = Path.GetFileName(absolutePath);
            Extension    = Path.GetExtension(absolutePath).ToLowerInvariant();
            FolderLabel  = folderLabel;
            Descriptor   = AssetTypeRegistry.Resolve(Extension);
        }
    }

    /// <summary>
    /// A node in the recursive directory tree exposed by <see cref="AssetDatabase.RootNodes"/>.
    /// Each node represents one directory; its <see cref="Files"/> list contains the
    /// directly-contained (non-skipped) assets.  Sub-directories are in <see cref="ChildFolders"/>.
    /// </summary>
    public sealed class AssetFolderNode
    {
        /// <summary>Display label for this node (directory name, or root label like "Content").</summary>
        public string Label { get; }

        /// <summary>Absolute path of the directory this node represents.</summary>
        public string AbsolutePath { get; }

        /// <summary>
        /// Project-relative path used as a stable persistence key
        /// (e.g. "Content/Sprites").  Forward-slash separated.
        /// </summary>
        public string RelativePath { get; }

        /// <summary>Direct child directories (sorted alphabetically by label).</summary>
        public List<AssetFolderNode> ChildFolders { get; } = new();

        /// <summary>Files directly inside this directory (not in sub-directories).</summary>
        public List<AssetItem> Files { get; } = new();

        public AssetFolderNode(string label, string absolutePath, string relativePath)
        {
            Label        = label;
            AbsolutePath = absolutePath;
            RelativePath = relativePath;
        }
    }

    public sealed class AssetDatabase : IDisposable
    {

        public static AssetDatabase Instance { get; internal set; }

        // Extensions that are never shown in the Asset Browser.
        private static readonly HashSet<string> SkippedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".xnb", ".mgcb", ".meta", ".manifest",
        };

    
        private static readonly HashSet<string> OsJunkFileNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "Thumbs.db", "ehthumbs.db", "ehthumbs_vista.db", "desktop.ini", "Icon\r",
        };

        // Editor byproducts, never assets; a .meta sidecar for one of these is a dangling GUID in waiting.
        private static readonly HashSet<string> TransientExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            // Generic scratch and backups
            ".tmp", ".temp", ".bak", ".old", ".orig", ".rej", ".save", ".autosave",

            // vim swap files
            ".swp", ".swo", ".swn", ".swm", ".swl", ".swx", ".kate-swp",

            // Partial writes
            ".part", ".partial", ".filepart", ".crdownload", ".crswap", ".download",

            // Locks, pids, logs
            ".lock", ".lck", ".pid", ".log",

            // IDE session and build noise
            ".sublime-workspace", ".suo", ".ncb", ".vsidx", ".tlog", ".cache",
        };

        // JetBrains atomic-save markers
        private static readonly string[] TransientNameMarkers = { "___jb_tmp___", "___jb_old___" };

        // Never assets, and the folders that dominate a repository's file count.
        private static readonly HashSet<string> TransientDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "bin", "obj", "node_modules", "temp", "tmp", "__pycache__",
            "$RECYCLE.BIN", "System Volume Information",
        };

        /// <summary>Dot-prefixed folders (.git, .vs, .idea, .claude, …) are tool state, never assets.</summary>
        private static bool IsTransientDirectoryName(string name) =>
            !string.IsNullOrEmpty(name) && (name[0] == '.' || TransientDirectoryNames.Contains(name));

        /// <summary>True when a directory between <paramref name="root"/> and the path is transient; the root itself is not judged.</summary>
        private static bool IsUnderTransientDirectory(string root, string path)
        {
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(path))
                return false;

            string relative;
            try
            {
                relative = Path.GetRelativePath(root, path);
            }
            catch
            {
                return false;
            }

            // Outside the root.
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                return false;

            var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            for (var i = 0; i < segments.Length - 1; i++)
            {
                if (IsTransientDirectoryName(segments[i]))
                    return true;
            }

            return false;
        }

        private static bool IsTransientFile(string path)
        {
            var name = Path.GetFileName(path);
            if (string.IsNullOrEmpty(name))
                return true;

            // Hidden dotfiles (.DS_Store, .gitkeep, …) are never assets. This alone covers
            // .DS_Store, but keep the explicit set below for Windows junk that isn't a dotfile.
            if (name[0] == '.')
                return true;

            if (OsJunkFileNames.Contains(name))
                return true;

            // Trailing '~' (backup) or leading '~' (e.g. '~$' Office lock files).
            if (name[name.Length - 1] == '~' || name[0] == '~')
                return true;

            // emacs autosave files are '#name#' (its lock file '.#name' is already caught above).
            if (name.Length > 1 && name[0] == '#' && name[name.Length - 1] == '#')
                return true;

            foreach (var marker in TransientNameMarkers)
            {
                if (name.Contains(marker, StringComparison.Ordinal))
                    return true;
            }

            return TransientExtensions.Contains(Path.GetExtension(path));
        }

    
        private static bool IsScriptFile(string path)
        {
            var ext = Path.GetExtension(path);
            return ext.Equals(".cs", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Debounce window for FSW events, in ms. Mirrors <c>ScriptWatcher</c>.</summary>
        private const double FswDebounceMs = 600;

        private readonly List<AssetItem> _items = new();
        private bool _isIndexed = false;

        // Grouped view: folder-label → items in that folder (kept for backward compat).
        private readonly Dictionary<string, List<AssetItem>> _byFolder =
            new(StringComparer.OrdinalIgnoreCase);

        // Recursive tree view (replaces _byFolder in the Asset Browser UI).
        private readonly List<AssetFolderNode> _rootNodes = new();

        // Both maps are guarded by _guidLock; all mutations go through the private helpers.
        private readonly object _guidLock = new();
        private readonly Dictionary<Guid, string>   _guidToPath = new();    // guid  → abs path
        private readonly Dictionary<string, Guid>   _pathToGuid = new(StringComparer.OrdinalIgnoreCase); // abs path → guid

        // GUIDs whose asset file was deleted but whose GUID we keep around so references
        // can report "dangling" rather than silently failing.
        private readonly HashSet<Guid> _danglingGuids = new();

        private readonly Dictionary<Guid, string> _dataAssetTypeIds = new();

        private readonly List<FileSystemWatcher> _watchers = new();
        private readonly Timer _debounceTimer;
        private readonly object _fswLock = new();
        private readonly List<FileSystemEventArgs> _pendingEvents = new(); // Pending raw FSW events accumulated during the debounce window.

        public AssetDatabase()
        {
            _debounceTimer = new Timer(FswDebounceMs) { AutoReset = false };
            _debounceTimer.Elapsed += OnDebounceElapsed;
        }

        /// <summary>All cached asset items. Empty until <see cref="Refresh"/> has been called.</summary>
        public IReadOnlyList<AssetItem> Items => _items;

        /// <summary>Assets grouped by their source-folder label.</summary>
        public IReadOnlyDictionary<string, List<AssetItem>> ByFolder => _byFolder;

        /// <summary>
        /// Root-level folder nodes for the recursive directory tree.
        /// Each entry corresponds to one project root folder (Content, Data, Scenes, …).
        /// Each node's <see cref="AssetFolderNode.ChildFolders"/> recursively mirrors the
        /// on-disk hierarchy under that root.
        /// </summary>
        public IReadOnlyList<AssetFolderNode> RootNodes => _rootNodes;

        /// <summary>Whether the index has been built at least once.</summary>
        public bool IsIndexed => _isIndexed;

        /// <summary>
        /// (Re)scans all project folders, rebuilds the in-memory browser index, ensures all
        /// discovered assets have a <c>.meta</c> sidecar, and (re)installs the
        /// <see cref="FileSystemWatcher"/>s.
        ///
        /// Safe to call repeatedly; does nothing useful if no project is loaded.
        /// </summary>
        public void Refresh()
        {
            _items.Clear();
            _byFolder.Clear();
            _rootNodes.Clear();

            TearDownWatchers();

            var project = ProjectManager.Instance?.CurrentProject;
            if (project == null)
            {
                _isIndexed = true;
                return;
            }

            // Ordered list of (folder path, display label).
            var folders = new (string Path, string Label)[]
            {
                (project.ContentsFolder, "Content"),
                (project.DataFolder,     "Data"),
                (project.ScriptsFolder,  "Scripts"),
                (project.EffectsFolder,  "Effects"),
                (project.ScenesFolder,   "Scenes"),
                (project.PrefabsFolder,  "Prefabs"),
            };

            foreach (var (folderPath, label) in folders)
            {
                if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
                    continue;

                string relRoot = ToProjectRelativePath(folderPath);
                var rootNode = BuildFolderNode(folderPath, label, relRoot);
                _rootNodes.Add(rootNode);

                ScanFolderFlat(folderPath, label);

                InstallWatcher(folderPath);
            }

            _isIndexed = true;
            EditorDebug.Log($"AssetDatabase: indexed {_items.Count} asset(s).", "AssetDatabase");

            // Bake the GUID→path map so published builds can resolve assets by GUID at runtime
            // (the runtime has no AssetDatabase). Written to Data/ so it ships with the game.
            WriteManifest();
        }

        /// <summary>
        /// Returns the stable GUID for <paramref name="absolutePath"/>, creating and persisting
        /// a <c>.meta</c> sidecar on first call for files that don't have one yet.
        ///
        /// Returns <see cref="Guid.Empty"/> when the path is null / empty.
        /// </summary>
        public Guid GetOrCreateGuid(string absolutePath)
        {
            if (string.IsNullOrEmpty(absolutePath))
                return Guid.Empty;

            // Never create a sidecar for transient temp/backup files.
            if (IsTransientFile(absolutePath))
                return Guid.Empty;

            // Scripts carry their identity in source via [ComponentId]; no .meta sidecar.
            if (IsScriptFile(absolutePath))
                return Guid.Empty;

            // Non-asset and transient files never get a GUID sidecar, whoever asks.
            if (SkippedExtensions.Contains(Path.GetExtension(absolutePath)) || IsTransientFile(absolutePath))
                return Guid.Empty;

            lock (_guidLock)
            {
                if (_pathToGuid.TryGetValue(absolutePath, out var existingGuid))
                {
                    // Self-heal: a prior move may have left the forward map stale or dangling while the
                    // reverse map still knew this path. Re-anchor the GUID to where the file actually is so
                    // a Refresh (which runs after every move) always rebuilds a correct GUID -> path map.
                    if (existingGuid != Guid.Empty &&
                        (!_guidToPath.TryGetValue(existingGuid, out var forward) ||
                         !string.Equals(forward, absolutePath, StringComparison.OrdinalIgnoreCase)))
                    {
                        _guidToPath[existingGuid] = absolutePath;
                        _danglingGuids.Remove(existingGuid);
                    }
                    return existingGuid;
                }
            }

            var guid = ReadOrCreateMeta(absolutePath);

            lock (_guidLock)
            {
                RegisterGuidUnsafe(guid, absolutePath);
            }

            return guid;
        }

        /// <summary>
        /// The <c>@assetType</c> of a <c>.vasset</c>, or null if that GUID is not one.
        /// </summary>
        public string GetDataAssetTypeId(Guid guid)
        {
            if (guid == Guid.Empty)
                return null;

            lock (_guidLock)
            {
                if (_dataAssetTypeIds.TryGetValue(guid, out var cached))
                    return cached;
            }

            var path = GetPath(guid);
            if (string.IsNullOrEmpty(path) ||
                !path.EndsWith(Voltage.Data.DataAssetIO.FileExtension, StringComparison.OrdinalIgnoreCase))
                return null;

            var id = Voltage.Data.DataAssetIO.PeekAssetTypeId(path);
            if (id != null)
            {
                lock (_guidLock)
                    _dataAssetTypeIds[guid] = id;
            }

            return id;
        }

        /// <summary>Drops the cached <c>@assetType</c> after a content change.</summary>
        private void InvalidateDataAssetTypeId(Guid guid)
        {
            if (guid == Guid.Empty)
                return;

            lock (_guidLock)
                _dataAssetTypeIds.Remove(guid);
        }

        /// <summary>
        /// Returns the current absolute path for <paramref name="guid"/>, or
        /// <c>null</c> when the GUID is unknown or dangling.
        /// </summary>
        public string GetPath(Guid guid)
        {
            if (guid == Guid.Empty)
                return null;

            lock (_guidLock)
            {
                _guidToPath.TryGetValue(guid, out var path);
                return path;
            }
        }

        /// <summary>
        /// Returns true when the given GUID was once registered but its backing file has
        /// since been deleted.
        /// </summary>
        public bool IsDangling(Guid guid)
        {
            lock (_guidLock)
            {
                return _danglingGuids.Contains(guid);
            }
        }

        /// <summary>
        /// Resolves an <see cref="AssetReference"/> to the current absolute path of the asset.
        ///
        /// Resolution order:
        /// <list type="number">
        ///   <item><description>GUID map — most reliable, survives renames.</description></item>
        ///   <item><description>
        ///     <c>HintPath</c> (project-relative) — used when the GUID is absent or unknown.
        ///     If a file is found at the hint location, its GUID is registered / updated
        ///     so future lookups succeed via the GUID map (self-heal).
        ///   </description></item>
        /// </list>
        ///
        /// Returns <c>null</c> when the asset cannot be located by either strategy.
        /// </summary>
        public string Resolve(AssetReference reference)
        {
            // 1. GUID path.
            if (reference.Guid != Guid.Empty)
            {
                var byGuid = GetPath(reference.Guid);
                if (!string.IsNullOrEmpty(byGuid) && File.Exists(byGuid))
                    return byGuid;
            }

            // 2. HintPath fallback.
            if (!string.IsNullOrEmpty(reference.HintPath))
            {
                var absoluteHint = HintToAbsolute(reference.HintPath);
                if (!string.IsNullOrEmpty(absoluteHint) && File.Exists(absoluteHint))
                {
                    // Self-heal: associate the (possibly new) GUID with this path.
                    if (reference.Guid != Guid.Empty)
                    {
                        lock (_guidLock)
                        {
                            RegisterGuidUnsafe(reference.Guid, absoluteHint);
                        }
                    }
                    else
                    {
                        // Path-only reference — ensure a sidecar exists and register the GUID.
                        GetOrCreateGuid(absoluteHint);
                    }
                    return absoluteHint;
                }
            }

            return null;
        }

        /// <summary>
        /// Builds an <see cref="AssetReference"/> for <paramref name="absolutePath"/>,
        /// creating the <c>.meta</c> sidecar if necessary.
        /// </summary>
        public AssetReference GetReference(string absolutePath)
        {
            if (string.IsNullOrEmpty(absolutePath))
                return AssetReference.Empty;

            var guid     = GetOrCreateGuid(absolutePath);
            var hintPath = ToProjectRelativePath(absolutePath);
            return new AssetReference(guid, hintPath);
        }

        public void Dispose()
        {
            TearDownWatchers();
            _debounceTimer.Dispose();
        }

        /// <summary>
        /// Recursively builds an <see cref="AssetFolderNode"/> tree for the given directory.
        /// Also ensures every discovered file has a .meta GUID sidecar.
        /// </summary>
        private AssetFolderNode BuildFolderNode(string absPath, string label, string relPath)
        {
            var node = new AssetFolderNode(label, absPath, relPath);

            // Enumerate direct files.
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(absPath, "*.*", SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex)
            {
                EditorDebug.Log($"AssetDatabase: failed to enumerate files in '{absPath}': {ex.Message}", "AssetDatabase");
                files = Array.Empty<string>();
            }

            foreach (var filePath in files)
            {
                var ext = Path.GetExtension(filePath);
                if (SkippedExtensions.Contains(ext) || IsTransientFile(filePath))
                    continue;

                GetOrCreateGuid(filePath);
                node.Files.Add(new AssetItem(filePath, label));
            }

            // Sort files alphabetically by name.
            node.Files.Sort((a, b) => string.Compare(a.FileName, b.FileName, StringComparison.OrdinalIgnoreCase));

            // Recurse into subdirectories (alphabetical).
            IEnumerable<string> subDirs;
            try
            {
                subDirs = Directory.EnumerateDirectories(absPath);
            }
            catch (Exception ex)
            {
                EditorDebug.Log($"AssetDatabase: failed to enumerate subdirs in '{absPath}': {ex.Message}", "AssetDatabase");
                subDirs = Array.Empty<string>();
            }

            var sortedDirs = new List<string>(subDirs);
            sortedDirs.Sort(StringComparer.OrdinalIgnoreCase);

            foreach (var subDir in sortedDirs)
            {
                var childLabel = Path.GetFileName(subDir);

                // Pruned here so build output and .git are never walked.
                if (IsTransientDirectoryName(childLabel))
                    continue;

                // Build a stable relative path key: forward-slash separated.
                var childRel = relPath.TrimEnd('/') + "/" + childLabel;
                var childNode = BuildFolderNode(subDir, childLabel, childRel);
                node.ChildFolders.Add(childNode);
            }

            return node;
        }

        /// <summary>
        /// Flat scan that populates <see cref="_items"/> and <see cref="_byFolder"/>
        /// (legacy data structures kept for backward compatibility).
        /// </summary>
        private void ScanFolderFlat(string rootPath, string label)
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(rootPath, "*.*", SearchOption.AllDirectories);
            }
            catch (Exception ex)
            {
                EditorDebug.Log($"AssetDatabase: failed to scan '{rootPath}': {ex.Message}", "AssetDatabase");
                return;
            }

            foreach (var filePath in files)
            {
                var ext = Path.GetExtension(filePath);
                if (SkippedExtensions.Contains(ext) || IsTransientFile(filePath)
                    || IsUnderTransientDirectory(rootPath, filePath))
                    continue;

                Utils.ProjectLoadProgress.Report(filePath);

                // GUIDs are already created by BuildFolderNode — GetOrCreateGuid is idempotent.
                GetOrCreateGuid(filePath);

                var item = new AssetItem(filePath, label);
                _items.Add(item);

                if (!_byFolder.TryGetValue(label, out var bucket))
                {
                    bucket = new List<AssetItem>();
                    _byFolder[label] = bucket;
                }

                bucket.Add(item);
            }
        }

        /// <summary>Path of the sidecar for a given asset absolute path.</summary>
        private static string MetaPath(string absolutePath) => absolutePath + ".meta";

        /// <summary>
        /// Reads the GUID from the companion <c>.meta</c> file, creating it if absent or
        /// unparseable.  Returns <see cref="Guid.Empty"/> only on an unrecoverable IO error.
        /// </summary>
        private static Guid ReadOrCreateMeta(string assetAbsolutePath)
        {
            var metaPath = MetaPath(assetAbsolutePath);

            if (File.Exists(metaPath))
            {
                try
                {
                    var json = File.ReadAllText(metaPath);
                    var meta = Voltage.Persistence.Json.FromJson<AssetMetaData>(json);
                    if (meta != null && Guid.TryParse(meta.Guid, out var parsedGuid) && parsedGuid != Guid.Empty)
                        return parsedGuid;
                }
                catch (Exception ex)
                {
                    EditorDebug.Log($"AssetDatabase: corrupt .meta '{metaPath}', regenerating. ({ex.Message})", "AssetDatabase");
                }
            }

            var newGuid = Guid.NewGuid();
            WriteMeta(metaPath, newGuid);
            return newGuid;
        }

        private static void WriteMeta(string metaPath, Guid guid)
        {
            try
            {
                var meta = new AssetMetaData { Guid = guid.ToString() };
                var json = Voltage.Persistence.Json.ToJson(meta, new Voltage.Persistence.JsonSettings
                {
                    PrettyPrint = true,
                    TypeNameHandling = Voltage.Persistence.TypeNameHandling.None,
                });
                File.WriteAllText(metaPath, json);
            }
            catch (Exception ex)
            {
                EditorDebug.Log($"AssetDatabase: failed to write .meta '{metaPath}': {ex.Message}", "AssetDatabase");
            }
        }

        /// <summary>
        /// Registers or updates a GUID ↔ path pair.  Must be called inside <c>lock(_guidLock)</c>.
        /// </summary>
        private void RegisterGuidUnsafe(Guid guid, string absolutePath)
        {
            if (guid == Guid.Empty)
                return;

            // If this path was previously associated with a different GUID, remove the stale reverse entry.
            if (_pathToGuid.TryGetValue(absolutePath, out var oldGuid) && oldGuid != guid)
                _guidToPath.Remove(oldGuid);

            _guidToPath[guid]         = absolutePath;
            _pathToGuid[absolutePath] = guid;
            _danglingGuids.Remove(guid); // no longer dangling if we have a path
        }

        /// <summary>
        /// Removes a path from the maps, marking its GUID as dangling.
        /// Must be called inside <c>lock(_guidLock)</c>.
        /// </summary>
        private void RemovePathUnsafe(string absolutePath)
        {
            if (_pathToGuid.TryGetValue(absolutePath, out var guid))
            {
                _pathToGuid.Remove(absolutePath);

                // Only drop the forward mapping if it still points at THIS path. A cross-folder move
                // arrives as Created(new) + Deleted(old);
                if (_guidToPath.TryGetValue(guid, out var current) &&
                    string.Equals(current, absolutePath, StringComparison.OrdinalIgnoreCase))
                {
                    _guidToPath.Remove(guid);
                    _danglingGuids.Add(guid);
                    _dataAssetTypeIds.Remove(guid);
                }
            }
        }

        private void InstallWatcher(string folderPath)
        {
            try
            {
                var watcher = new FileSystemWatcher(folderPath)
                {
                    // Watch all files in subdirectories.
                    NotifyFilter       = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                    IncludeSubdirectories = true,
                    EnableRaisingEvents   = true,
                };

                watcher.Created += OnFswEvent;
                watcher.Deleted += OnFswEvent;
                watcher.Renamed += OnFswRename;

                watcher.Changed += OnFswEvent;

                _watchers.Add(watcher);
            }
            catch (Exception ex)
            {
                EditorDebug.Log($"AssetDatabase: failed to install FSW on '{folderPath}': {ex.Message}", "AssetDatabase");
            }
        }

        private void TearDownWatchers()
        {
            foreach (var w in _watchers)
                w.Dispose();
            _watchers.Clear();
        }

        private void OnFswEvent(object sender, FileSystemEventArgs e)
        {
            var ext = Path.GetExtension(e.FullPath);
            if (SkippedExtensions.Contains(ext) || IsTransientFile(e.FullPath))
                return;

            // Churn inside obj/, .git/ and the like must not wake the debounce.
            if (IsUnderTransientDirectory((sender as FileSystemWatcher)?.Path, e.FullPath))
                return;

            lock (_fswLock)
            {
                _pendingEvents.Add(e);
                _debounceTimer.Stop();
                _debounceTimer.Start();
            }
        }

        private void OnFswRename(object sender, RenamedEventArgs e)
        {
            // Skip .meta files — they move silently alongside the asset.
            var extNew = Path.GetExtension(e.FullPath);
            var extOld = Path.GetExtension(e.OldFullPath);
            if (SkippedExtensions.Contains(extNew) && SkippedExtensions.Contains(extOld))
                return;
            if (IsTransientFile(e.FullPath) && IsTransientFile(e.OldFullPath))
                return;

            // Both sides, so a file moved OUT of a skipped folder into the project is still imported.
            var watchRoot = (sender as FileSystemWatcher)?.Path;
            if (IsUnderTransientDirectory(watchRoot, e.FullPath)
                && IsUnderTransientDirectory(watchRoot, e.OldFullPath))
                return;

            lock (_fswLock)
            {
                _pendingEvents.Add(e);
                _debounceTimer.Stop();
                _debounceTimer.Start();
            }
        }

        private void OnDebounceElapsed(object sender, ElapsedEventArgs e)
        {
            List<FileSystemEventArgs> batch;
            lock (_fswLock)
            {
                if (_pendingEvents.Count == 0)
                    return;
                batch = new List<FileSystemEventArgs>(_pendingEvents);
                _pendingEvents.Clear();
            }

            // Process on the main thread: schedule via Core.Schedule so GUID map writes
            // are always single-threaded with respect to other editor state.
            Core.Schedule(0f, false, this, _ => ProcessFswBatch(batch));
        }

        private void ProcessFswBatch(List<FileSystemEventArgs> batch)
        {
            bool needsBrowserRefresh = false;

            foreach (var ev in batch)
            {
                try
                {
                    if (ev is RenamedEventArgs rename)
                    {
                        HandleRename(rename.OldFullPath, rename.FullPath);
                        needsBrowserRefresh = true;
                    }
                    else if (ev.ChangeType == WatcherChangeTypes.Deleted)
                    {
                        HandleDelete(ev.FullPath);
                        needsBrowserRefresh = true;
                    }
                    else if (ev.ChangeType == WatcherChangeTypes.Created)
                    {
                        if (!SkippedExtensions.Contains(Path.GetExtension(ev.FullPath)))
                        {
                            GetOrCreateGuid(ev.FullPath);
                            needsBrowserRefresh = true;
                        }
                    }
                    else if (ev.ChangeType == WatcherChangeTypes.Changed)
                    {
                        HandleContentChanged(ev.FullPath);
                    }
                }
                catch (Exception ex)
                {
                    EditorDebug.Log($"AssetDatabase: error processing FSW event for '{ev.FullPath}': {ex.Message}", "AssetDatabase");
                }
            }

            if (needsBrowserRefresh)
                Refresh(); // Rebuild the browser index (also reinstalls watchers).
        }

        /// <summary>
        /// Handles a rename/move: the .meta travels alongside the file, so the GUID is
        /// preserved — we just update the path in the GUID maps.
        /// </summary>
        private void HandleRename(string oldAbsPath, string newAbsPath)
        {
            lock (_guidLock)
            {
                if (_pathToGuid.TryGetValue(oldAbsPath, out var guid))
                {
                    _pathToGuid.Remove(oldAbsPath);
                    _pathToGuid[newAbsPath] = guid;
                    _guidToPath[guid]       = newAbsPath;
                }
                // Even if we didn't have it cached, the new path may now exist — let
                // GetOrCreateGuid pick up the .meta that moved with the file.
            }

            // Ensure the new path is indexed (the .meta will have moved with the file).
            GetOrCreateGuid(newAbsPath);
        }

        /// <summary>
        /// Only <c>.vasset</c> files act on a content-only write: the cached header is dropped and any live instance is re-read <b>in place</b>, so an open window and a running play session both pick up an external edit without a restart.
        /// </summary>
        private void HandleContentChanged(string absolutePath)
        {
            if (string.IsNullOrEmpty(absolutePath) ||
                !absolutePath.EndsWith(Voltage.Data.DataAssetIO.FileExtension, StringComparison.OrdinalIgnoreCase))
                return;

            Guid guid;
            lock (_guidLock)
                _pathToGuid.TryGetValue(absolutePath, out guid);

            InvalidateDataAssetTypeId(guid);
            Voltage.Data.DataAssetCache.ReloadPath(absolutePath);
        }

        /// <summary>
        /// Handles a delete: marks the GUID dangling without dropping it so that outstanding
        /// references can report "missing asset" rather than silently resolving to null.
        /// </summary>
        private void HandleDelete(string absolutePath)
        {
            lock (_guidLock)
            {
                RemovePathUnsafe(absolutePath);
            }

            EditorDebug.Log($"AssetDatabase: asset deleted, GUID now dangling — '{absolutePath}'", "AssetDatabase");
        }

        /// <summary>
        /// Converts a project-relative hint path (forward slashes) to an absolute path
        /// using the current project root.  Returns null when no project is loaded.
        /// </summary>
        private static string HintToAbsolute(string hintPath)
        {
            if (string.IsNullOrEmpty(hintPath))
                return null;

            var project = ProjectManager.Instance?.CurrentProject;
            if (project == null)
                return null;

            var combined = Path.Combine(project.ProjectPath,
                hintPath.Replace('/', Path.DirectorySeparatorChar));
            return Path.GetFullPath(combined);
        }

        /// <summary>
        /// Converts an absolute path to a project-relative forward-slash path for storage
        /// in <see cref="AssetReference.HintPath"/>.
        /// </summary>
        /// <summary>
        /// Writes <c>Data/assets.manifest</c> — a flat <c>{ "guid": "project-relative/path" }</c> JSON
        /// map of every tracked asset — so a published build (which has no AssetDatabase) can resolve
        /// assets by GUID at runtime via <see cref="Voltage.Assets.AssetManifest"/>. Called at the end
        /// of every <see cref="Refresh"/> so it stays current. The <c>.manifest</c> extension is in
        /// <see cref="SkippedExtensions"/>, so writing it does not retrigger the FileSystemWatcher.
        /// </summary>
        private void WriteManifest()
        {
            var project = ProjectManager.Instance?.CurrentProject;
            if (project == null || string.IsNullOrEmpty(project.DataFolder))
                return;

            try
            {
                var entries = new List<(string Guid, string Path)>();

                lock (_guidLock)
                {
                    foreach (var kvp in _guidToPath)
                    {
                        var rel = ToProjectRelativePath(kvp.Value);
                        if (string.IsNullOrEmpty(rel))
                            continue;

                        entries.Add((kvp.Key.ToString(), rel));
                    }
                }

                entries.Sort((a, b) => string.CompareOrdinal(a.Guid, b.Guid));

                var sb = new StringBuilder();
                sb.Append("{\n");

                for (var i = 0; i < entries.Count; i++)
                {
                    if (i > 0)
                        sb.Append(",\n");

                    sb.Append("  \"").Append(entries[i].Guid).Append("\": \"")
                      .Append(EscapeJsonString(entries[i].Path)).Append('"');
                }

                sb.Append("\n}\n");

                Directory.CreateDirectory(project.DataFolder);

                var manifestPath = Path.Combine(project.DataFolder, "assets.manifest");
                var contents = sb.ToString();

                // Refresh runs often; rewriting identical bytes would still churn the file's timestamp.
                if (File.Exists(manifestPath) &&
                    string.Equals(File.ReadAllText(manifestPath), contents, StringComparison.Ordinal))
                    return;

                File.WriteAllText(manifestPath, contents);
            }
            catch (Exception ex)
            {
                EditorDebug.Log($"AssetDatabase: failed to write asset manifest: {ex.Message}", "AssetDatabase");
            }
        }

        private static string EscapeJsonString(string s) =>
            s.Replace("\\", "\\\\").Replace("\"", "\\\"");

        private static string ToProjectRelativePath(string absolutePath)
        {
            if (string.IsNullOrEmpty(absolutePath))
                return absolutePath;

            try
            {
                var project = ProjectManager.Instance?.CurrentProject;
                if (project != null)
                    return CrossPlatformPath.GetRelativePathForStorage(project.ProjectPath, absolutePath);
            }
            catch { /* fall through */ }

            return CrossPlatformPath.Normalize(absolutePath).Replace(CrossPlatformPath.Sep, '/');
        }
    }
}
