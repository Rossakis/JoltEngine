using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ImGuiNET;
using Voltage.Assets;
using Voltage.Editor.Hotkeys;
using Voltage.Editor.Assets;
using Voltage.Editor.DebugUtils;
using Voltage.Editor.ImGuiCore;
using Voltage.Editor.Persistence;
using Voltage.Editor.ProjectFile;
using Voltage.Editor.Utils;
using Voltage.Utils;
using Num = System.Numerics;

namespace Voltage.Editor.Windows;

/// <summary>
/// Shows project assets in a recursive directory tree that mirrors the on-disk
/// folder hierarchy under each project root. 
/// </summary>
public class AssetBrowserWindow : IDisposable
{
    /// <summary>ImGui drag-drop payload identifier for asset drags from the Asset Browser.</summary>
    public const string DragDropPayloadId = "VOLTAGE_ASSET_REF";

    /// <summary>
    /// Shared slot: the Asset Browser deposits the <see cref="AssetReference"/> here when a
    /// drag starts.  The drop target (<see cref="SceneGraphWindow"/>) reads this slot on
    /// <c>AcceptDragDropPayload</c>.
    /// </summary>
    public static AssetReference DraggedReference;

    /// <summary>
    /// Absolute path of the file currently being dragged, used for folder-to-folder MOVES
    /// (distinct from <see cref="DraggedReference"/>, which is for dropping into the scene).
    /// Set on drag start; consumed by a folder node's drop target.
    /// </summary>
    public static string DraggedSourcePath;

    /// <summary>Every asset in the drag. Holds one entry for a single-row drag.</summary>
    public static readonly List<string> DraggedSourcePaths = new();

    private const string WindowTitle     = "Asset Browser###AssetBrowserWindow";
    private const float  IconSize        = 20f;
    private const float  IconTextSpacing = 6f;

    // Separator used to join/split collapsed-node paths in the persistent string.
    private const char CollapsedNodeSep = '|';
    private const string CollapsedNodeKey = "AssetBrowser.CollapsedNodes";

    // Persistent user-defined shortcuts (project-relative paths to folders or files),
    // shown in a "Shortcuts" section at the very top. Stored pipe-separated like collapsed nodes.
    private const string ShortcutsKey = "AssetBrowser.Shortcuts";

    public bool IsOpen { get; set; } = true;

    private readonly AssetDatabase _db;
    private string _searchFilter = string.Empty;

    // Persistent collapsed-node state.
    // Key: project-relative node path (e.g. "Content/Sprites").
    // A node is in this set when it is COLLAPSED (same semantics as before —
    // tree nodes start open, user can close them).
    private readonly HashSet<string> _collapsedNodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly PersistentString _collapsedNodesSetting;

    // Ordered list of project-relative shortcut paths (folders or files). Order = insertion order.
    private readonly List<string> _shortcuts = new();
    private readonly PersistentString _shortcutsSetting;

    private string _hoveredFolderPath = null;

    private readonly SdlFileDropWatcher _sdlDropWatcher;


    private readonly List<string> _copiedPaths = new();

    // Multi-selection, in click order. _selectedFilePath is the primary (last touched) entry.
    private readonly List<string> _selection = new();
    private string _selectionAnchor;

    // Visible rows in draw order, rebuilt each frame so a Shift range can be resolved after the pass.
    private readonly List<string> _visibleOrder = new();
    private string _pendingRangeTo;

    private string _selectedFilePath
    {
        get => _selection.Count > 0 ? _selection[_selection.Count - 1] : null;
        set => SelectSingle(value);
    }

    private bool IsSelected(string path) =>
        path != null && _selection.Contains(path, StringComparer.OrdinalIgnoreCase);

    private void SelectSingle(string path)
    {
        _selection.Clear();
        if (!string.IsNullOrEmpty(path))
            _selection.Add(path);

        _selectionAnchor = path;
    }

    private void ToggleSelection(string path)
    {
        var index = _selection.FindIndex(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
            _selection.RemoveAt(index);
        else
            _selection.Add(path);

        _selectionAnchor = path;
    }

    /// <summary>Resolves a Shift-click against the completed draw order, so nesting and filtering are honoured.</summary>
    private void ResolvePendingRange()
    {
        if (_pendingRangeTo == null)
            return;

        var to = _visibleOrder.FindIndex(p => string.Equals(p, _pendingRangeTo, StringComparison.OrdinalIgnoreCase));
        var from = _selectionAnchor == null
            ? -1
            : _visibleOrder.FindIndex(p => string.Equals(p, _selectionAnchor, StringComparison.OrdinalIgnoreCase));

        _pendingRangeTo = null;

        if (to < 0)
            return;

        if (from < 0)
            from = to;

        _selection.Clear();
        for (var i = Math.Min(from, to); i <= Math.Max(from, to); i++)
            _selection.Add(_visibleOrder[i]);
    }

    private void RemoveFromSelection(string path) =>
        _selection.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));

    private void ReplaceInSelection(string oldPath, string newPath)
    {
        for (var i = 0; i < _selection.Count; i++)
        {
            if (string.Equals(_selection[i], oldPath, StringComparison.OrdinalIgnoreCase))
                _selection[i] = newPath;
        }

        if (string.Equals(_selectionAnchor, oldPath, StringComparison.OrdinalIgnoreCase))
            _selectionAnchor = newPath;
    }

    /// <summary>Selected files, newest last. Folders are never part of this selection.</summary>
    private List<string> SelectedPaths() => new(_selection);

    private bool _showDeleteConfirmation = false;
    private readonly List<string> _filesToDelete = new();
    private string _fileToDelete = null;
    // When the pending delete targets a directory (recursive) rather than a single file.
    private bool _deleteIsFolder = false;

    private string _renamingFilePath = null;
    // Absolute path of the folder being inline-renamed (mutually exclusive with _renamingFilePath).
    private string _renamingFolderPath = null;
    private string _renameBuffer = string.Empty;
    private bool _renameFocusPending = false;

    private string _renameCandidatePath = null;
    private double _renameCandidateTime;

    // Set by in-tree operations (move/paste/duplicate/rename) instead of calling _db.Refresh()
    // directly: Refresh() rebuilds _db.RootNodes, which DrawTree is enumerating at that moment,
    // so the rebuild is deferred to the end of Draw() to avoid "collection was modified".
    private bool _refreshRequested = false;

    private int _seenAssetTypeVersion = -1;

    #region Ping (reveal + highlight)

    private const double PingDuration = 1.6;

    // Set by the AssetReference / PrefabReference inspectors; consumed on the next Draw.
    private static string _pendingPingPath;

    private string _pingPath;
    private double _pingStartedAt;
    private bool _pingScrollPending;

    /// <summary>Reveals an asset: opens the window, expands its folders, selects it, scrolls to it, flashes it.</summary>
    public static void PingAsset(string absolutePath)
    {
        if (string.IsNullOrEmpty(absolutePath))
            return;

        _pendingPingPath = absolutePath;

        var manager = Core.GetGlobalManager<ImGuiManager>();
        if (manager != null)
            manager.ShowAssetBrowser = true;
    }

    private void ConsumePingRequest()
    {
        if (_pendingPingPath == null)
            return;

        var path = _pendingPingPath;
        _pendingPingPath = null;

        if (!File.Exists(path))
        {
            EditorDebug.Log($"AssetBrowser: cannot reveal '{path}' — the file does not exist.", "AssetBrowser");
            return;
        }

        RevealFolders(path);

        _selectedFilePath = path;
        _pingPath = path;
        _pingStartedAt = ImGui.GetTime();
        _pingScrollPending = true;

        // The search filter would hide the asset we are revealing.
        _searchFilter = string.Empty;
    }

    /// <summary>Expands every folder on the way to the file so its row is actually drawn.</summary>
    private void RevealFolders(string absolutePath)
    {
        foreach (var root in _db.RootNodes)
            RevealFolders(root, absolutePath);
    }

    private bool RevealFolders(AssetFolderNode node, string absolutePath)
    {
        var found = node.Files.Any(f =>
            string.Equals(f.AbsolutePath, absolutePath, StringComparison.OrdinalIgnoreCase));

        if (!found)
        {
            foreach (var child in node.ChildFolders)
            {
                if (RevealFolders(child, absolutePath))
                {
                    found = true;
                    break;
                }
            }
        }

        if (found)
            _collapsedNodes.Remove(node.RelativePath);

        return found;
    }

    private bool IsPinging(string absolutePath) =>
        _pingPath != null
        && string.Equals(_pingPath, absolutePath, StringComparison.OrdinalIgnoreCase)
        && ImGui.GetTime() - _pingStartedAt < PingDuration;

    private Num.Vector4 PingColor()
    {
        var elapsed = ImGui.GetTime() - _pingStartedAt;
        var fade = (float)Math.Max(0.0, 1.0 - elapsed / PingDuration);
        var pulse = 0.55f + 0.45f * (float)Math.Abs(Math.Sin(elapsed * 8.0));

        return new Num.Vector4(1f, 0.72f, 0.2f, fade * pulse);
    }

    #endregion

    private bool _showCreateAssetPopup = false;
    private string _createAssetFolder = null;
    private string _createAssetName = string.Empty;
    private string _createAssetLabel = "Asset";
    private string _createAssetExtension = string.Empty;
    private Action<string> _createAssetWriter = null;

    public AssetBrowserWindow()
    {
        _db = new AssetDatabase();
        AssetDatabase.Instance = _db;

        // Restore persisted collapse state.
        _collapsedNodesSetting = new PersistentString(CollapsedNodeKey, string.Empty);
        LoadCollapsedState();

        // Restore persisted shortcuts.
        _shortcutsSetting = new PersistentString(ShortcutsKey, string.Empty);
        LoadShortcuts();

        _sdlDropWatcher = new SdlFileDropWatcher();
    }

    public void OnProjectLoaded()
    {
        // Reload persisted collapse + shortcut state with the new project context.
        LoadCollapsedState();
        LoadShortcuts();
        _db.Refresh();
    }

    public void OnProjectUnloaded()
    {
        _db.Refresh();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _sdlDropWatcher.Dispose();
        _db.Dispose();
        if (AssetDatabase.Instance == _db)
            AssetDatabase.Instance = null;
    }

    public void Draw()
    {
        if (!IsOpen)
            return;

        if (!_db.IsIndexed)
            _db.Refresh();

        DrainSdlFileDrops();
        ConsumePingRequest();

        var open = IsOpen;
        Gui.Begin(WindowTitle, ref open);
        IsOpen = open;

        if (!IsOpen)
        {
            Gui.End();
            return;
        }

        DrawToolbar();
        ImGui.Separator();

        // Reset the hovered-folder tracker each frame before drawing the tree.
        _hoveredFolderPath = null;
        _visibleOrder.Clear();

        if (!ProjectManager.Instance.HasActiveProject)
        {
            ImGui.TextColored(new Num.Vector4(0.6f, 0.6f, 0.6f, 1f), "No project loaded.");
        }
        else if (_db.RootNodes.Count == 0)
        {
            ImGui.TextColored(new Num.Vector4(0.6f, 0.6f, 0.6f, 1f), "No assets found. Press Refresh.");
        }
        else
        {
            // Shortcuts section (only rendered when at least one shortcut is defined).
            DrawShortcuts();
            DrawTree();
        }

        // Needs the completed _visibleOrder, so it runs after the tree pass.
        ResolvePendingRange();

        HandleKeyboardShortcuts();

        PromotePendingRename();

        Gui.End();

        // Must be outside Begin/End to be modal.
        DrawDeleteConfirmationPopup();
        DrawCreateAssetPopup();

        if (_seenAssetTypeVersion != AssetTypeRegistry.Version)
        {
            _seenAssetTypeVersion = AssetTypeRegistry.Version;
            AssetBrowserIcons.ClearCache();
            _refreshRequested = true;
        }

        if (_refreshRequested)
        {
            _refreshRequested = false;
            _db.Refresh();
        }
    }

    /// <summary>
    /// Dequeues SDL DROPFILE paths and copies them into the project.
    /// Target folder = <see cref="_hoveredFolderPath"/> (updated each frame by the tree
    /// renderer); if not hovered over the browser or null, falls back to
    /// <c>project.ContentsFolder</c>.
    /// </summary>
    private void DrainSdlFileDrops()
    {
        if (!_db.IsIndexed)
            return;

        _sdlDropWatcher.DrainPending(sourcePath =>
        {
            if (!File.Exists(sourcePath))
            {
                EditorDebug.Log($"AssetBrowser: SDL drop path '{sourcePath}' is not a file — skipped.", "AssetBrowser");
                return;
            }

            // Determine target directory.
            string targetDir = _hoveredFolderPath;
            if (string.IsNullOrEmpty(targetDir) || !Directory.Exists(targetDir))
            {
                var project = ProjectManager.Instance?.CurrentProject;
                if (project == null)
                {
                    EditorDebug.Log("AssetBrowser: SDL drop received but no project loaded — ignoring.", "AssetBrowser");
                    return;
                }
                targetDir = project.ContentsFolder;
            }

            try
            {
                CopyFileCollisionSafe(sourcePath, targetDir);
            }
            catch (Exception ex)
            {
                EditorDebug.Log($"AssetBrowser: failed to copy '{sourcePath}' → '{targetDir}': {ex.Message}", "AssetBrowser");
            }

            _db.Refresh();
        });
    }

    private void DrawToolbar()
    {
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 80f);
        Gui.InputTextWithHint("##AssetSearch", "Search...", ref _searchFilter, 128);

        ImGui.SameLine();

        if (Gui.Button("Refresh"))
            _db.Refresh();

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Rescan all project folders and rebuild the asset index.");
    }

    private void DrawTree()
    {
        foreach (var root in _db.RootNodes)
        {
            // Skip a root nested inside another root (e.g. Data/Scenes shows under Data) — drawing it
            // as a separate top-level root would duplicate the view.
            if (IsNestedUnderAnotherRoot(root))
                continue;

            ImGui.PushID(root.RelativePath);
            DrawFolderNode(root);
            ImGui.PopID();
            VoltageEditorUtils.SmallVerticalSpace();
        }
    }

    // True when root's directory lives inside another root's directory (already reachable by expanding that one).
    private bool IsNestedUnderAnotherRoot(AssetFolderNode root)
    {
        foreach (var other in _db.RootNodes)
        {
            if (ReferenceEquals(other, root))
                continue;
            if (IsPathUnder(root.AbsolutePath, other.AbsolutePath))
                return true;
        }
        return false;
    }

    // True when childAbs is the same as, or nested under, ancestorAbs.
    private static bool IsPathUnder(string childAbs, string ancestorAbs)
    {
        if (string.IsNullOrEmpty(childAbs) || string.IsNullOrEmpty(ancestorAbs))
            return false;

        var child    = Path.GetFullPath(childAbs).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var ancestor = Path.GetFullPath(ancestorAbs).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return child.StartsWith(ancestor + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    // Recursively renders a folder node and its children. A non-null shortcutRelPath marks this as a
    // Shortcuts-section root, adding "Remove Shortcut" to its context menu.
    private void DrawFolderNode(AssetFolderNode node, string shortcutRelPath = null)
    {
        bool hasSearchFilter = !string.IsNullOrWhiteSpace(_searchFilter);

        // When searching, skip folders that have no matching descendants.
        if (hasSearchFilter && !FolderHasMatchingItems(node))
            return;

        // While this folder is being renamed, show an editable field in place of the tree node.
        if (string.Equals(_renamingFolderPath, node.AbsolutePath, StringComparison.OrdinalIgnoreCase))
        {
            DrawFolderRenameInput(node);
            return;
        }

        bool isCollapsed = _collapsedNodes.Contains(node.RelativePath);
        bool isProtected = IsProtectedFolder(node.AbsolutePath);

        // Build tree node flags: open by default unless explicitly collapsed.
        var flags = ImGuiTreeNodeFlags.OpenOnArrow
                  | ImGuiTreeNodeFlags.SpanAvailWidth;
        if (!isCollapsed)
            flags |= ImGuiTreeNodeFlags.DefaultOpen;

        bool nodeOpen = Gui.TreeNodeEx(node.Label, flags);

        // Track hover for SDL file-drop targeting.
        if (ImGui.IsItemHovered())
            _hoveredFolderPath = node.AbsolutePath;

        // Right-click context menu: folder operations.
        DrawFolderContextMenu(node, isProtected, shortcutRelPath);

        // Drag SOURCE: only non-protected folders can be moved; protected roots/subfolders stay put.
        if (!isProtected && ImGui.BeginDragDropSource(ImGuiDragDropFlags.None))
        {
            DraggedSourcePath = node.AbsolutePath;
            DraggedSourcePaths.Clear();
            DraggedReference  = default; // folders aren't scene/inspector droppable
            unsafe
            {
                byte sentinel = 0;
                ImGui.SetDragDropPayload(DragDropPayloadId, (IntPtr)(&sentinel), 1);
            }
            ImGuiSafe.TextColoredSafe(new Num.Vector4(0.8f, 0.85f, 1f, 1f), $"{node.Label}  (folder)");
            ImGui.EndDragDropSource();
        }

        // Accept an in-browser MOVE: dropping an asset/folder here relocates it (carrying its .meta
        // sidecar / whole subtree), preserving GUIDs so references don't break.
        if (ImGui.BeginDragDropTarget())
        {
            unsafe
            {
                var payload = ImGui.AcceptDragDropPayload(DragDropPayloadId);
                if (payload.NativePtr != null && !string.IsNullOrEmpty(DraggedSourcePath))
                {
                    if (Directory.Exists(DraggedSourcePath))
                    {
                        MoveFolder(DraggedSourcePath, node.AbsolutePath);
                    }
                    else
                    {
                        var moving = DraggedSourcePaths.Count > 0
                            ? new List<string>(DraggedSourcePaths)
                            : new List<string> { DraggedSourcePath };

                        foreach (var path in moving)
                            MoveAsset(path, node.AbsolutePath);
                    }

                    DraggedSourcePaths.Clear();
                    DraggedSourcePath = null;
                }
            }
            ImGui.EndDragDropTarget();
        }

        // Toggle persistence whenever the user opens or closes the node.
        if (nodeOpen && isCollapsed)
        {
            _collapsedNodes.Remove(node.RelativePath);
            SaveCollapsedState();
        }
        else if (!nodeOpen && !isCollapsed)
        {
            _collapsedNodes.Add(node.RelativePath);
            SaveCollapsedState();
        }

        if (nodeOpen)
        {
            // Recurse into child folders first (alphabetical).
            foreach (var child in node.ChildFolders)
            {
                ImGui.PushID(child.RelativePath);
                DrawFolderNode(child);
                ImGui.PopID();
            }

            // Then draw file leaves.
            for (int i = 0; i < node.Files.Count; i++)
            {
                var item = node.Files[i];

                if (hasSearchFilter &&
                    !item.FileName.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                ImGui.PushID(i);
                DrawAssetRow(item, node.AbsolutePath);
                ImGui.PopID();
            }

            ImGui.TreePop();
        }
    }

    /// <summary>Returns true if <paramref name="node"/> or any descendant has a file
    /// whose name matches the current search filter.</summary>
    private bool FolderHasMatchingItems(AssetFolderNode node)
    {
        foreach (var f in node.Files)
        {
            if (f.FileName.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        foreach (var child in node.ChildFolders)
        {
            if (FolderHasMatchingItems(child))
                return true;
        }

        return false;
    }

    private void DrawAssetRow(AssetItem item, string parentFolderPath)
    {
        var iconId = AssetBrowserIcons.GetIconId(item.Descriptor);

        if (iconId != IntPtr.Zero)
        {
            ImGui.Image(iconId, new Num.Vector2(IconSize, IconSize));
            ImGui.SameLine(0, IconTextSpacing);
        }

        // While this row is being renamed, show an editable field instead of the label.
        if (string.Equals(_renamingFilePath, item.AbsolutePath, StringComparison.OrdinalIgnoreCase))
        {
            DrawRenameInput(item);
            return;
        }

        _visibleOrder.Add(item.AbsolutePath);

        bool isSelected = IsSelected(item.AbsolutePath);
        bool wasSelected = isSelected;

        bool isPinging = IsPinging(item.AbsolutePath);
        if (isPinging)
        {
            var ping = PingColor();
            ImGui.PushStyleColor(ImGuiCol.Header, ping);
            ImGui.PushStyleColor(ImGuiCol.HeaderHovered, ping);
            ImGui.PushStyleColor(ImGuiCol.HeaderActive, ping);
        }

        bool clicked = Gui.Selectable(item.FileName, isSelected || isPinging,
            ImGuiSelectableFlags.AllowDoubleClick,
            new Num.Vector2(ImGui.GetContentRegionAvail().X, IconSize));

        if (isPinging)
        {
            ImGui.PopStyleColor(3);

            if (_pingScrollPending)
            {
                ImGui.SetScrollHereY(0.5f);
                _pingScrollPending = false;
            }
        }

        // Track hover on file rows — the parent folder is still the drop destination.
        if (ImGui.IsItemHovered())
            _hoveredFolderPath = parentFolderPath;

        if (clicked && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
        {
            // Double-click ACTIVATES the asset (and cancels any pending rename): scenes load, prefabs open
            // in an isolated edit scene. Other kinds have no activation. Rename now happens via a slow
            // second click (below) or the context menu, matching other editors.
            _renameCandidatePath = null;
            _selectedFilePath = item.AbsolutePath;

            var reference = _db.GetReference(item.AbsolutePath);

            // A type's own opener wins over its kind, so a plugin file type opens in the right tool.
            if (item.Descriptor.Open != null)
                item.Descriptor.Open(item.AbsolutePath);
            else if (item.Descriptor.Kind == AssetKind.Scene)
                DropHandlers.DropScene(reference);
            else if (item.Descriptor.Kind == AssetKind.Prefab)
                DropHandlers.OpenPrefabIsolated(reference);
            else if (item.Descriptor.Kind == AssetKind.Tileset)
                Core.GetGlobalManager<ImGuiManager>()?.TilesetEditorWindow.Open(item.AbsolutePath);
            else if (item.Descriptor.Kind == AssetKind.Data)
                Core.GetGlobalManager<ImGuiManager>()?.DataAssetWindow.Open(item.AbsolutePath);
        }
        else if (clicked)
        {
            var io = ImGui.GetIO();
            var additive = io.KeyCtrl || io.KeySuper;

            if (io.KeyShift)
            {
                // Resolved after the draw pass, once _visibleOrder holds every row.
                _pendingRangeTo = item.AbsolutePath;
                _renameCandidatePath = null;
            }
            else if (additive)
            {
                ToggleSelection(item.AbsolutePath);
                _renameCandidatePath = null;
            }
            else if (wasSelected && _selection.Count == 1)
            {
                // Second click on the lone selected row arms a delayed rename.
                _renameCandidatePath = item.AbsolutePath;
                _renameCandidateTime = ImGui.GetTime();
            }
            else
            {
                SelectSingle(item.AbsolutePath);
                _renameCandidatePath = null;
            }
        }

        // Full-path + GUID tooltip on hover.
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGuiSafe.TextColoredSafe(new Num.Vector4(0.7f, 0.9f, 1f, 1f), item.Descriptor.Kind.ToString());
            ImGuiSafe.TextSafe(item.AbsolutePath);

            if (AssetDatabase.Instance != null)
            {
                var guid = AssetDatabase.Instance.GetOrCreateGuid(item.AbsolutePath);
                if (guid != Guid.Empty)
                    ImGuiSafe.TextColoredSafe(new Num.Vector4(0.5f, 0.8f, 0.5f, 1f), guid.ToString());
            }

            ImGui.EndTooltip();
        }

        var assetPopupId = $"##ctx_{item.FileName}";
        ImGuiPopupUtils.ConstrainHeight(assetPopupId);

        if (Gui.BeginPopupContextItem(assetPopupId))
        {
            // Right-clicking inside a multi-selection keeps it; otherwise the click reselects.
            if (!IsSelected(item.AbsolutePath))
                SelectSingle(item.AbsolutePath);

            var targets = SelectedPaths();
            var many = targets.Count > 1;
            var suffix = many ? $" ({targets.Count} assets)" : string.Empty;

            if (Gui.MenuItem($"Copy{suffix}"))
                CopyToInternalClipboard(targets);

            bool hasCopied = _copiedPaths.Count > 0;
            if (!hasCopied)
                ImGui.BeginDisabled();

            if (Gui.MenuItem("Paste"))
                PasteFiles(parentFolderPath);

            if (!hasCopied)
                ImGui.EndDisabled();

            if (Gui.MenuItem($"Duplicate{suffix}"))
            {
                foreach (var path in targets)
                    DuplicateFile(path);
            }

            ImGui.BeginDisabled(many);
            if (Gui.MenuItem("Rename"))
                BeginRename(item.AbsolutePath);
            ImGui.EndDisabled();

            if (many && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Rename works on a single asset.");

            ImGui.BeginDisabled(many);
            if (Gui.MenuItem("Create Shortcut"))
                AddShortcut(item.AbsolutePath);
            ImGui.EndDisabled();

            ImGui.Separator();

            if (Gui.MenuItem("Open In File Explorer"))
                FileExplorerUtils.Reveal(item.AbsolutePath);

            ImGui.Separator();

            if (Gui.MenuItem($"Delete{suffix}"))
                RequestDeleteMany(targets);

            Gui.EndPopup();
        }

        if (ImGui.BeginDragDropSource(ImGuiDragDropFlags.None))
        {
            // A drag is not a rename gesture — cancel any pending delayed rename so releasing the drag
            // doesn't trip into rename mode.
            _renameCandidatePath = null;

            // Dragging an unselected row reselects it first, so the drag always matches what is highlighted.
            if (!IsSelected(item.AbsolutePath))
                SelectSingle(item.AbsolutePath);

            // Every asset can be dragged to another folder to MOVE it; stash the source paths.
            DraggedSourcePath = item.AbsolutePath;
            DraggedSourcePaths.Clear();
            DraggedSourcePaths.AddRange(SelectedPaths());

            // Droppable types additionally carry an AssetReference so they can be dropped into
            // the scene / inspector. Non-droppable types (scripts, etc.) are move-only.
            bool isDroppable = item.Descriptor.DropFactory != null;
            // default(AssetReference).IsEmpty == true, so scene/inspector drop targets ignore
            // move-only (non-droppable) assets.
            DraggedReference = isDroppable ? _db.GetReference(item.AbsolutePath) : default;

            unsafe
            {
                byte sentinel = 0;
                ImGui.SetDragDropPayload(DragDropPayloadId, (IntPtr)(&sentinel), 1);
            }

            var dragLabel = DraggedSourcePaths.Count > 1
                ? $"{DraggedSourcePaths.Count} assets"
                : item.FileName;

            if (isDroppable && DraggedSourcePaths.Count <= 1)
                ImGuiSafe.TextSafe(dragLabel);
            else
                ImGuiSafe.TextColoredSafe(new Num.Vector4(0.8f, 0.85f, 1f, 1f), $"{dragLabel}  (move)");

            ImGui.EndDragDropSource();
        }
    }

    /// <summary>
    /// Promotes a pending rename candidate into an actual inline rename once the double-click window has
    /// elapsed without a double-click (which would have activated the asset and cleared the candidate).
    /// Waiting for the window — and for the mouse button to be released — is what distinguishes a slow
    /// "click… click" rename from a fast double-click activation and from the start of a drag.
    /// </summary>
    private void PromotePendingRename()
    {
        if (_renameCandidatePath == null)
            return;

        // Still holding the button (or mid double-click): wait. A drag would have cleared the candidate.
        if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
            return;

        double window = ImGui.GetIO().MouseDoubleClickTime + 0.02; // small epsilon past the dbl-click window
        if (ImGui.GetTime() - _renameCandidateTime < window)
            return;

        // Only rename if the candidate is still the selected row (selection may have moved on).
        if (string.Equals(_selectedFilePath, _renameCandidatePath, StringComparison.OrdinalIgnoreCase))
            BeginRename(_renameCandidatePath);

        _renameCandidatePath = null;
    }

    /// <summary>Enters inline-rename mode for the given file, seeding the buffer with its
    /// current name (without extension) and selecting it on the first frame.</summary>
    private void BeginRename(string absolutePath)
    {
        _renamingFilePath = absolutePath;
        _renameBuffer = Path.GetFileNameWithoutExtension(absolutePath);
        _renameFocusPending = true;
        _selectedFilePath = absolutePath;
    }

    /// <summary>
    /// Draws the inline rename text field for the row currently being renamed.
    /// Enter (or clicking away) commits; Escape cancels; an empty/whitespace name commits as a no-op.
    /// </summary>
    private void DrawRenameInput(AssetItem item)
    {
        if (_renameFocusPending)
        {
            ImGui.SetKeyboardFocusHere();
            _renameFocusPending = false;
        }

        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        bool enter = Gui.InputText($"##rename_{item.AbsolutePath}", ref _renameBuffer, 256,
            ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);

        // Escape must be checked before deactivation: pressing Escape also deactivates the field.
        bool escape = ImGui.IsKeyPressed(ImGuiKey.Escape, false);
        bool deactivated = ImGui.IsItemDeactivated();

        if (enter)
        {
            CommitRename(item.AbsolutePath, _renameBuffer);
            _renamingFilePath = null;
        }
        else if (escape)
        {
            _renamingFilePath = null; // cancel — no change
        }
        else if (deactivated)
        {
            // Clicked away — commit like a file explorer.
            CommitRename(item.AbsolutePath, _renameBuffer);
            _renamingFilePath = null;
        }
    }

    /// <summary>
    /// Renames <paramref name="oldPath"/> to use <paramref name="newBaseName"/> (extension preserved),
    /// carrying the <c>.meta</c> sidecar so the asset's GUID — and every reference to it — survives.
    /// For a C# script it also renames the matching component class (and its constructors) inside the
    /// file so the class name tracks the file name; the component's stable <c>[ComponentId]</c> keeps
    /// scene references intact regardless. An empty/whitespace or unchanged name is a no-op, and an
    /// existing target name is refused rather than overwritten.
    /// </summary>
    private void CommitRename(string oldPath, string newBaseName)
    {
        newBaseName = newBaseName?.Trim();
        if (string.IsNullOrEmpty(newBaseName))
            return; // empty -> leave the name unchanged

        var dir     = Path.GetDirectoryName(oldPath);
        var ext     = Path.GetExtension(oldPath);
        var oldBase = Path.GetFileNameWithoutExtension(oldPath);

        if (string.Equals(newBaseName, oldBase, StringComparison.Ordinal))
            return; // unchanged

        if (string.IsNullOrEmpty(dir) || newBaseName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            EditorDebug.Log($"AssetBrowser: invalid file name '{newBaseName}'.", "AssetBrowser");
            return;
        }

        var newPath = Path.Combine(dir, newBaseName + ext);
        if (File.Exists(newPath))
        {
            EditorDebug.Log($"AssetBrowser: cannot rename — '{newBaseName}{ext}' already exists.", "AssetBrowser");
            return;
        }

        try
        {
            // For scripts, rename the matching class first (best-effort, in-file). The [ComponentId]
            // attribute is left untouched, so scene references stay valid even though the class moved.
            if (string.Equals(ext, ".cs", StringComparison.OrdinalIgnoreCase))
                Scripting.ScriptClassRenamer.RenameClassInFile(oldPath, oldBase, newBaseName);

            File.Move(oldPath, newPath);

            // Carry the .meta sidecar (binary assets only — scripts have none) to preserve the GUID.
            var oldMeta = oldPath + ".meta";
            if (File.Exists(oldMeta))
                File.Move(oldMeta, newPath + ".meta");

            ReplaceInSelection(oldPath, newPath);

            EditorDebug.Log($"AssetBrowser: renamed '{Path.GetFileName(oldPath)}' → '{newBaseName}{ext}'", "AssetBrowser");
            _refreshRequested = true;
        }
        catch (Exception ex)
        {
            EditorDebug.Log($"AssetBrowser: rename failed for '{oldPath}': {ex.Message}", "AssetBrowser");
        }
    }

    private void HandleKeyboardShortcuts()
    {
        // Only handle shortcuts when this window has focus.
        if (!ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
            return;

        if (EditorHotkeys.Pressed(EditorHotkeys.AssetCopy) && _selection.Count > 0)
            CopyToInternalClipboard(SelectedPaths());

        if (EditorHotkeys.Pressed(EditorHotkeys.AssetPaste) && _copiedPaths.Count > 0)
        {
            string targetDir = _hoveredFolderPath;
            if (string.IsNullOrEmpty(targetDir) || !Directory.Exists(targetDir))
            {
                var project = ProjectManager.Instance?.CurrentProject;
                targetDir = project?.ContentsFolder;
            }
            if (!string.IsNullOrEmpty(targetDir))
                PasteFiles(targetDir);
        }

        if (EditorHotkeys.Pressed(EditorHotkeys.AssetDuplicate) && _selection.Count > 0)
        {
            foreach (var path in SelectedPaths())
                DuplicateFile(path);
        }

        if (EditorHotkeys.Pressed(EditorHotkeys.AssetDelete) && _selection.Count > 0)
            RequestDeleteMany(SelectedPaths());
    }

    /// <summary>Copies several paths to the internal clipboard, replacing whatever was there.</summary>
    private void CopyToInternalClipboard(List<string> paths)
    {
        _copiedPaths.Clear();
        foreach (var path in paths)
        {
            if (!string.IsNullOrEmpty(path))
                _copiedPaths.Add(path);
        }
    }

    /// <summary>Copies the path to the internal clipboard (does NOT touch the OS clipboard).</summary>
    private void CopyToInternalClipboard(string absolutePath)
    {
        _copiedPaths.Clear();
        _copiedPaths.Add(absolutePath);
        EditorDebug.Log($"AssetBrowser: copied '{Path.GetFileName(absolutePath)}' to internal clipboard.", "AssetBrowser");
    }

    /// <summary>
    /// Pastes all internally-copied files into <paramref name="targetDir"/>.
    /// Each pasted file gets a NEW GUID via Refresh/GetOrCreateGuid — the source .meta is NOT copied.
    /// </summary>
    private void PasteFiles(string targetDir)
    {
        if (_copiedPaths.Count == 0 || string.IsNullOrEmpty(targetDir))
            return;

        bool anyPasted = false;
        foreach (var sourcePath in _copiedPaths)
        {
            if (!File.Exists(sourcePath))
            {
                EditorDebug.Log($"AssetBrowser: paste source '{sourcePath}' no longer exists — skipped.", "AssetBrowser");
                continue;
            }

            try
            {
                CopyFileCollisionSafe(sourcePath, targetDir);
                anyPasted = true;
            }
            catch (Exception ex)
            {
                EditorDebug.Log($"AssetBrowser: paste failed for '{sourcePath}': {ex.Message}", "AssetBrowser");
            }
        }

        if (anyPasted)
            _refreshRequested = true;
    }

    /// <summary>
    /// Duplicates a file in its own folder with a " (1)" suffix (or " (2)", etc.).
    /// The duplicate gets a NEW GUID — the .meta sidecar is NOT copied.
    /// </summary>
    private void DuplicateFile(string absolutePath)
    {
        if (!File.Exists(absolutePath))
        {
            EditorDebug.Log($"AssetBrowser: cannot duplicate '{absolutePath}' — file not found.", "AssetBrowser");
            return;
        }

        try
        {
            string dir = Path.GetDirectoryName(absolutePath)!;
            CopyFileCollisionSafe(absolutePath, dir);
            _refreshRequested = true;
        }
        catch (Exception ex)
        {
            EditorDebug.Log($"AssetBrowser: duplicate failed for '{absolutePath}': {ex.Message}", "AssetBrowser");
        }
    }

    /// <summary>Triggers the delete confirmation popup for the given file or folder.</summary>
    private void RequestDelete(string absolutePath, bool isFolder)
    {
        _fileToDelete = absolutePath;
        _deleteIsFolder = isFolder;
        _filesToDelete.Clear();

        if (!isFolder && !string.IsNullOrEmpty(absolutePath))
            _filesToDelete.Add(absolutePath);

        _showDeleteConfirmation = true;
    }

    /// <summary>Triggers the delete confirmation popup for a whole selection of assets.</summary>
    private void RequestDeleteMany(List<string> paths)
    {
        if (paths == null || paths.Count == 0)
            return;

        if (paths.Count == 1)
        {
            RequestDelete(paths[0], isFolder: false);
            return;
        }

        _fileToDelete = null;
        _deleteIsFolder = false;
        _filesToDelete.Clear();
        _filesToDelete.AddRange(paths);
        _showDeleteConfirmation = true;
    }

    /// <summary>
    /// Draws the modal delete-confirmation popup (mirrors the pattern in SceneGraphWindow).
    /// Deletes the file AND its .meta sidecar on confirmation.
    /// </summary>
    private void DrawDeleteConfirmationPopup()
    {
        if (_showDeleteConfirmation)
        {
            ImGui.OpenPopup("asset-delete-confirmation");
            _showDeleteConfirmation = false;
        }

        var center = new Num.Vector2(Screen.Width * 0.5f, Screen.Height * 0.5f);
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Num.Vector2(0.5f, 0.5f));

        bool open = true;
        if (Gui.BeginPopupModal("asset-delete-confirmation", ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Text(_deleteIsFolder ? "Delete Folder" : "Delete Asset");
            ImGui.Separator();

            string fileName = _fileToDelete != null ? Path.GetFileName(_fileToDelete) : string.Empty;

            if (_deleteIsFolder)
                ImGuiSafe.TextWrappedSafe(
                    $"Are you sure you want to delete the folder '{fileName}' and everything inside it?");
            else if (_filesToDelete.Count > 1)
                ImGuiSafe.TextWrappedSafe($"Are you sure you want to delete these {_filesToDelete.Count} assets?");
            else
                ImGuiSafe.TextWrappedSafe($"Are you sure you want to delete '{fileName}'?");

            if (!_deleteIsFolder && _filesToDelete.Count > 1)
            {
                ImGui.BeginChild("delete-list", new Num.Vector2(360, 90), true);
                foreach (var path in _filesToDelete)
                    ImGui.TextUnformatted(Path.GetFileName(path));
                ImGui.EndChild();
            }
            ImGui.TextColored(new Num.Vector4(1f, 0.6f, 0.2f, 1f), "This action cannot be undone!");

            VoltageEditorUtils.MediumVerticalSpace();

            float buttonWidth  = 80f;
            float spacing      = 10f;
            float totalWidth   = buttonWidth * 2 + spacing;
            float centerStart  = (ImGui.GetWindowSize().X - totalWidth) * 0.5f;
            ImGui.SetCursorPosX(centerStart);

            if (Gui.Button("Yes", new Num.Vector2(buttonWidth, 0)))
            {
                if (_deleteIsFolder)
                {
                    ExecuteDeleteFolder(_fileToDelete);
                }
                else
                {
                    foreach (var path in _filesToDelete)
                        ExecuteDelete(path);
                }

                _filesToDelete.Clear();
                _fileToDelete = null;
                ImGui.CloseCurrentPopup();
            }

            ImGui.SameLine();

            if (Gui.Button("No", new Num.Vector2(buttonWidth, 0)))
            {
                _filesToDelete.Clear();
                _fileToDelete = null;
                ImGui.CloseCurrentPopup();
            }

            Gui.EndPopup();
        }
    }

    /// <summary>
    /// Deletes a file and its companion .meta sidecar.
    /// Clears the selection and refreshes the index afterwards.
    /// </summary>
    private void ExecuteDelete(string absolutePath)
    {
        if (string.IsNullOrEmpty(absolutePath))
            return;

        try
        {
            if (File.Exists(absolutePath))
                File.Delete(absolutePath);

            // Remove the .meta sidecar so no orphan GUIDs clutter the project.
            string metaPath = absolutePath + ".meta";
            if (File.Exists(metaPath))
                File.Delete(metaPath);

            EditorDebug.Log($"AssetBrowser: deleted '{Path.GetFileName(absolutePath)}'.", "AssetBrowser");

            RemoveFromSelection(absolutePath);

            _db.Refresh();
        }
        catch (Exception ex)
        {
            EditorDebug.Log($"AssetBrowser: delete failed for '{absolutePath}': {ex.Message}", "AssetBrowser");
        }
    }

    /// <summary>
    /// Moves an asset into <paramref name="targetDir"/>, carrying its companion <c>.meta</c>
    /// sidecar along with it. Because the sidecar (which holds the stable GUID) travels with the
    /// file, every reference that points at this asset by GUID keeps resolving — the move does not
    /// break scenes or prefabs. Scripts have no sidecar (their identity lives in the
    /// <c>[ComponentId]</c> source attribute, which moves with the file content), so they are safe too.
    /// Same-folder drops are a no-op; name collisions get a " (1)" suffix without overwriting.
    /// </summary>
    private void MoveAsset(string sourcePath, string targetDir)
    {
        if (string.IsNullOrEmpty(sourcePath) || string.IsNullOrEmpty(targetDir))
            return;
        if (!File.Exists(sourcePath) || !Directory.Exists(targetDir))
            return;

        var sourceDir = Path.GetDirectoryName(sourcePath);
        if (string.Equals(sourceDir, targetDir, StringComparison.OrdinalIgnoreCase))
            return; // already in this folder

        string baseName = Path.GetFileNameWithoutExtension(sourcePath);
        string ext      = Path.GetExtension(sourcePath);
        string destPath = Path.Combine(targetDir, baseName + ext);

        if (File.Exists(destPath))
        {
            int n = 1;
            do
            {
                destPath = Path.Combine(targetDir, $"{baseName} ({n}){ext}");
                n++;
            }
            while (File.Exists(destPath));
        }

        try
        {
            File.Move(sourcePath, destPath);

            // Keep the GUID linkage intact: move the .meta sidecar to match the new file name.
            string srcMeta = sourcePath + ".meta";
            if (File.Exists(srcMeta))
                File.Move(srcMeta, destPath + ".meta");

            ReplaceInSelection(sourcePath, destPath);

            EditorDebug.Log($"AssetBrowser: moved '{Path.GetFileName(sourcePath)}' → '{targetDir}'", "AssetBrowser");
            _refreshRequested = true;
        }
        catch (Exception ex)
        {
            EditorDebug.Log($"AssetBrowser: move failed for '{sourcePath}': {ex.Message}", "AssetBrowser");
        }
    }

    /// <summary>
    /// Copies <paramref name="sourcePath"/> into <paramref name="targetDir"/>,
    /// resolving name collisions by appending " (1)", " (2)", … without silent overwrite.
    /// Does NOT copy the .meta sidecar — new assets always get a fresh GUID via Refresh.
    /// </summary>
    private static void CopyFileCollisionSafe(string sourcePath, string targetDir)
    {
        if (!File.Exists(sourcePath))
        {
            EditorDebug.Log($"AssetBrowser: source '{sourcePath}' is not a file — skipped.", "AssetBrowser");
            return;
        }

        string baseName = Path.GetFileNameWithoutExtension(sourcePath);
        string ext      = Path.GetExtension(sourcePath);
        string destPath = Path.Combine(targetDir, baseName + ext);

        // Resolve name collisions without silent overwrite.
        if (File.Exists(destPath))
        {
            int n = 1;
            do
            {
                destPath = Path.Combine(targetDir, $"{baseName} ({n}){ext}");
                n++;
            }
            while (File.Exists(destPath));
        }

        File.Copy(sourcePath, destPath, overwrite: false);
        EditorDebug.Log($"AssetBrowser: copied '{Path.GetFileName(sourcePath)}' → '{destPath}'", "AssetBrowser");
    }

    private void LoadCollapsedState()
    {
        _collapsedNodes.Clear();
        var stored = _collapsedNodesSetting.Value;
        if (string.IsNullOrEmpty(stored))
            return;

        foreach (var part in stored.Split(CollapsedNodeSep, StringSplitOptions.RemoveEmptyEntries))
            _collapsedNodes.Add(part);
    }

    private void SaveCollapsedState()
    {
        _collapsedNodesSetting.Value = string.Join(CollapsedNodeSep, _collapsedNodes);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // Folder operations: context menu, create, rename, move, delete, protection.
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    // Right-click menu for a folder. Rename/Delete only for non-protected folders; Add Folder and
    // Create Shortcut are always available.
    private void DrawFolderContextMenu(AssetFolderNode node, bool isProtected, string shortcutRelPath = null)
    {
        var folderPopupId = $"##folderctx_{node.RelativePath}";
        ImGuiPopupUtils.ConstrainHeight(folderPopupId);

        if (!Gui.BeginPopupContextItem(folderPopupId))
            return;

        // When this folder is the root of a Shortcuts entry, allow removing the shortcut here.
        if (shortcutRelPath != null)
        {
            if (Gui.MenuItem("Remove Shortcut"))
                RemoveShortcut(shortcutRelPath);
            ImGui.Separator();
        }

        if (Gui.MenuItem("Add Folder"))
            CreateFolder(node.AbsolutePath);

        if (Gui.BeginMenu("Create"))
        {
            DrawCreateAssetMenuItems(node.AbsolutePath);
            Gui.EndMenu();
        }

        if (Gui.MenuItem("Create Shortcut"))
            AddShortcut(node.AbsolutePath);

        ImGui.Separator();

        if (Gui.MenuItem("Open In File Explorer"))
            FileExplorerUtils.Reveal(node.AbsolutePath);

        ImGui.Separator();

        if (isProtected)
            ImGui.BeginDisabled();

        if (Gui.MenuItem("Rename"))
            BeginRenameFolder(node.AbsolutePath);

        ImGui.Separator();

        if (Gui.MenuItem("Delete"))
            RequestDelete(node.AbsolutePath, isFolder: true);

        if (isProtected)
        {
            ImGui.EndDisabled();
            ImGui.Separator();
            ImGui.TextColored(new Num.Vector4(0.6f, 0.6f, 0.6f, 1f), "(protected folder)");
        }

        Gui.EndPopup();
    }

    // True for folders that must not be moved/renamed/deleted: project roots and their standard
    // subfolders. User-created folders return false.
    private bool IsProtectedFolder(string absolutePath)
    {
        var project = ProjectManager.Instance?.CurrentProject;
        if (project == null)
            return true;

        string[] protectedPaths =
        {
            project.ProjectPath,
            project.ContentsFolder, project.DataFolder, project.ScriptsFolder,
            project.EffectsFolder, project.ScenesFolder, project.PrefabsFolder,
        };

        foreach (var p in protectedPaths)
        {
            if (!string.IsNullOrEmpty(p) && PathsEqual(p, absolutePath))
                return true;
        }
        return false;
    }

    private static bool PathsEqual(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            return false;
        return string.Equals(
            Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    // Creates a collision-safe "New Folder" under parentDir and enters inline-rename mode on it.
    private void CreateFolder(string parentDir)
    {
        if (string.IsNullOrEmpty(parentDir) || !Directory.Exists(parentDir))
            return;

        string newPath = Path.Combine(parentDir, "New Folder");
        int n = 1;
        while (Directory.Exists(newPath))
            newPath = Path.Combine(parentDir, $"New Folder ({n++})");

        try
        {
            Directory.CreateDirectory(newPath);
            EditorDebug.Log($"AssetBrowser: created folder '{newPath}'.", "AssetBrowser");
            // Rename it on the next frame (the node exists once the tree rebuilds).
            _renamingFolderPath = newPath;
            _renameBuffer = Path.GetFileName(newPath);
            _renameFocusPending = true;
            _refreshRequested = true;
        }
        catch (Exception ex)
        {
            EditorDebug.Log($"AssetBrowser: failed to create folder under '{parentDir}': {ex.Message}", "AssetBrowser");
        }
    }

    private void DrawCreateAssetMenuItems(string targetFolder)
    {
        foreach (var format in AssetFileRegistry.All.OrderBy(f => f.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var options = format.CreateOptions;
            if (options == null || options.Count == 0)
                continue;

            if (options.Count == 1)
            {
                var option = options[0];
                if (Gui.MenuItem(option.Label))
                    BeginCreateAsset(targetFolder, option.Label, option.DefaultFileName,
                        format.Extension, path => option.Write(path));
                continue;
            }

            if (!Gui.BeginMenu(format.DisplayName))
                continue;

            foreach (var option in options)
            {
                if (Gui.MenuItem(option.Label))
                    BeginCreateAsset(targetFolder, option.Label, option.DefaultFileName,
                        format.Extension, path => option.Write(path));
            }

            Gui.EndMenu();
        }
    }

    // Arms the create-asset popup for the given target folder, seeding a default name.
    private void BeginCreateAsset(string targetFolder, string label, string defaultName, string extension,
        Action<string> writer)
    {
        if (string.IsNullOrEmpty(targetFolder) || !Directory.Exists(targetFolder))
            return;

        _createAssetFolder = targetFolder;
        _createAssetLabel = label;
        _createAssetName = defaultName;
        _createAssetExtension = extension;
        _createAssetWriter = writer;
        _showCreateAssetPopup = true;
    }

    // Modal name-entry popup (mirrors the layout-save / delete-confirmation popups). Enter or Create
    // confirms; Escape or Cancel dismisses. An empty name disables Create.
    private void DrawCreateAssetPopup()
    {
        if (_showCreateAssetPopup)
        {
            ImGui.OpenPopup("create-asset");
            _showCreateAssetPopup = false;
        }

        var center = new Num.Vector2(Screen.Width * 0.5f, Screen.Height * 0.4f);
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Num.Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Num.Vector2(400, 0), ImGuiCond.Appearing);

        bool open = true;
        if (!Gui.BeginPopupModal("create-asset", ref open, ImGuiWindowFlags.AlwaysAutoResize))
            return;

        ImGui.Text($"New {_createAssetLabel}");
        ImGui.Separator();

        string folderLabel = _createAssetFolder != null ? Path.GetFileName(_createAssetFolder) : string.Empty;
        ImGui.TextColored(new Num.Vector4(0.6f, 0.6f, 0.6f, 1f), $"In folder: {folderLabel}");

        ImGui.Text("Enter name:");
        ImGui.SetNextItemWidth(350);
        bool enter = Gui.InputText("##createassetname", ref _createAssetName, 128,
            ImGuiInputTextFlags.EnterReturnsTrue);

        bool nameValid = !string.IsNullOrWhiteSpace(_createAssetName)
                       && _createAssetName.Trim().IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

        if (!string.IsNullOrWhiteSpace(_createAssetName) && !nameValid)
            ImGuiSafe.TextColoredSafe(new Num.Vector4(1f, 0.6f, 0.2f, 1f), "Invalid file name.");

        VoltageEditorUtils.MediumVerticalSpace();

        float buttonWidth = 100f;
        float spacing     = 10f;
        float totalWidth  = (buttonWidth * 2) + spacing;
        float centerStart = (ImGui.GetWindowSize().X - totalWidth) * 0.5f;
        ImGui.SetCursorPosX(centerStart);

        if (!nameValid)
            ImGui.BeginDisabled();

        bool confirm = Gui.Button("Create", new Num.Vector2(buttonWidth, 0)) || (enter && nameValid);

        if (!nameValid)
            ImGui.EndDisabled();

        if (confirm && nameValid)
        {
            CreateAsset(_createAssetFolder, _createAssetName);
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();

        if (Gui.Button("Cancel", new Num.Vector2(buttonWidth, 0)))
            ImGui.CloseCurrentPopup();

        Gui.EndPopup();
    }

    // Writes a fresh default asset of the armed kind into targetFolder, appending the extension if absent
    // and uniquifying the name to avoid overwriting an existing file, then requests a refresh so the
    // AssetDatabase catalogs it (GUID / .meta sidecar).
    private void CreateAsset(string targetFolder, string name)
    {
        if (string.IsNullOrEmpty(targetFolder) || !Directory.Exists(targetFolder))
            return;

        name = name?.Trim();
        if (string.IsNullOrEmpty(name))
            return;

        if (_createAssetWriter == null || string.IsNullOrEmpty(_createAssetExtension))
        {
            EditorDebug.Log("AssetBrowser: create requested with no asset kind armed.", "AssetBrowser");
            return;
        }

        string ext = _createAssetExtension;
        string kind = _createAssetLabel.ToLowerInvariant();

        // Accept a name the user typed with the extension already on it.
        string baseName = name;
        if (baseName.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            baseName = Path.GetFileNameWithoutExtension(baseName);

        if (string.IsNullOrEmpty(baseName) || baseName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            EditorDebug.Log($"AssetBrowser: invalid {kind} name '{name}'.", "AssetBrowser");
            return;
        }

        string path = Path.Combine(targetFolder, baseName + ext);

        // Uniquify rather than overwrite an existing file.
        if (File.Exists(path))
        {
            int n = 1;
            do
            {
                path = Path.Combine(targetFolder, $"{baseName} ({n}){ext}");
                n++;
            }
            while (File.Exists(path));
        }

        try
        {
            _createAssetWriter(path);
            EditorDebug.Log($"AssetBrowser: created {kind} '{Path.GetFileName(path)}'.", "AssetBrowser");

            // Re-index so the new file gets a GUID / .meta sidecar and shows up in the browser.
            _selectedFilePath = path;
            _refreshRequested = true;
        }
        catch (Exception ex)
        {
            EditorDebug.Log($"AssetBrowser: failed to create {kind} at '{path}': {ex.Message}", "AssetBrowser");
        }
    }

    private void BeginRenameFolder(string absolutePath)
    {
        _renamingFolderPath = absolutePath;
        _renamingFilePath = null;
        _renameBuffer = Path.GetFileName(absolutePath);
        _renameFocusPending = true;
    }

    // Draws the inline rename field for the folder currently being renamed.
    private void DrawFolderRenameInput(AssetFolderNode node)
    {
        if (_renameFocusPending)
        {
            ImGui.SetKeyboardFocusHere();
            _renameFocusPending = false;
        }

        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        bool enter = Gui.InputText($"##folderrename_{node.AbsolutePath}", ref _renameBuffer, 256,
            ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);

        bool escape = ImGui.IsKeyPressed(ImGuiKey.Escape, false);
        bool deactivated = ImGui.IsItemDeactivated();

        if (enter || deactivated)
        {
            CommitFolderRename(node.AbsolutePath, _renameBuffer);
            _renamingFolderPath = null;
        }
        else if (escape)
        {
            _renamingFolderPath = null; // cancel
        }
    }

    // Renames a folder on disk (the .meta sidecars inside travel with it, so GUIDs/references survive).
    // Unchanged/empty/invalid name is a no-op; an existing target is refused.
    private void CommitFolderRename(string oldPath, string newName)
    {
        newName = newName?.Trim();
        if (string.IsNullOrEmpty(newName))
            return;

        var parent  = Path.GetDirectoryName(oldPath);
        var oldName = Path.GetFileName(oldPath);
        if (string.Equals(newName, oldName, StringComparison.Ordinal))
            return;

        if (string.IsNullOrEmpty(parent) || newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            EditorDebug.Log($"AssetBrowser: invalid folder name '{newName}'.", "AssetBrowser");
            return;
        }

        var newPath = Path.Combine(parent, newName);
        if (Directory.Exists(newPath))
        {
            EditorDebug.Log($"AssetBrowser: cannot rename — folder '{newName}' already exists.", "AssetBrowser");
            return;
        }

        try
        {
            Directory.Move(oldPath, newPath);
            RepointShortcutsAfterMove(oldPath, newPath);
            EditorDebug.Log($"AssetBrowser: renamed folder '{oldName}' → '{newName}'.", "AssetBrowser");
            _refreshRequested = true;
        }
        catch (Exception ex)
        {
            EditorDebug.Log($"AssetBrowser: folder rename failed for '{oldPath}': {ex.Message}", "AssetBrowser");
        }
    }

    // Moves sourceDir into targetParentDir with its whole subtree (incl. .meta sidecars) so every GUID
    // reference keeps resolving. Refuses moves into itself/a descendant; skips no-op same-parent moves.
    private void MoveFolder(string sourceDir, string targetParentDir)
    {
        if (string.IsNullOrEmpty(sourceDir) || string.IsNullOrEmpty(targetParentDir))
            return;
        if (!Directory.Exists(sourceDir) || !Directory.Exists(targetParentDir))
            return;

        if (IsProtectedFolder(sourceDir))
            return; // defensive: protected folders aren't draggable, but never move one anyway

        // No-op if already directly inside the target.
        if (PathsEqual(Path.GetDirectoryName(sourceDir), targetParentDir))
            return;

        // Cannot move a folder into itself or a descendant of itself.
        if (PathsEqual(sourceDir, targetParentDir) || IsPathUnder(targetParentDir, sourceDir))
        {
            EditorDebug.Log("AssetBrowser: cannot move a folder into itself or its own subfolder.", "AssetBrowser");
            return;
        }

        string name = Path.GetFileName(sourceDir);
        string destPath = Path.Combine(targetParentDir, name);
        int n = 1;
        while (Directory.Exists(destPath))
            destPath = Path.Combine(targetParentDir, $"{name} ({n++})");

        try
        {
            Directory.Move(sourceDir, destPath);
            RepointShortcutsAfterMove(sourceDir, destPath);
            EditorDebug.Log($"AssetBrowser: moved folder '{name}' → '{targetParentDir}'.", "AssetBrowser");
            _refreshRequested = true;
        }
        catch (Exception ex)
        {
            EditorDebug.Log($"AssetBrowser: folder move failed for '{sourceDir}': {ex.Message}", "AssetBrowser");
        }
    }

    // Recursively deletes a folder (and every asset + .meta inside it), then refreshes.
    private void ExecuteDeleteFolder(string absolutePath)
    {
        if (string.IsNullOrEmpty(absolutePath) || !Directory.Exists(absolutePath))
            return;

        if (IsProtectedFolder(absolutePath))
        {
            EditorDebug.Log($"AssetBrowser: refusing to delete protected folder '{absolutePath}'.", "AssetBrowser");
            return;
        }

        try
        {
            Directory.Delete(absolutePath, recursive: true);
            RemoveShortcutsUnder(absolutePath);
            EditorDebug.Log($"AssetBrowser: deleted folder '{Path.GetFileName(absolutePath)}'.", "AssetBrowser");

            _selection.RemoveAll(p => IsPathUnder(p, absolutePath));

            _db.Refresh();
        }
        catch (Exception ex)
        {
            EditorDebug.Log($"AssetBrowser: folder delete failed for '{absolutePath}': {ex.Message}", "AssetBrowser");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // Shortcuts: a persistent quick-access list of folders/files, shown at the top of the browser.
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    // Draws the "Shortcuts" section (only when at least one exists). Folder shortcuts act as drop
    // targets; file shortcuts drag into the scene/inspector and activate on double-click. Each is
    // removable via its right-click menu.
    private void DrawShortcuts()
    {
        if (_shortcuts.Count == 0)
            return;

        if (!Gui.CollapsingHeader("Shortcuts", ImGuiTreeNodeFlags.DefaultOpen))
        {
            VoltageEditorUtils.SmallVerticalSpace();
            ImGui.Separator();
            return;
        }

        // Iterate a copy so a Remove during the loop doesn't invalidate enumeration.
        foreach (var relPath in new List<string>(_shortcuts))
        {
            string abs = ShortcutToAbsolute(relPath);
            ImGui.PushID($"shortcut::{relPath}");

            if (abs != null && Directory.Exists(abs))
                DrawFolderShortcut(relPath, abs);
            else if (abs != null && File.Exists(abs))
                DrawFileShortcut(relPath, abs);
            else
                DrawMissingShortcut(relPath);

            ImGui.PopID();
        }

        VoltageEditorUtils.SmallVerticalSpace();
        ImGui.Separator();
    }

    private void DrawFolderShortcut(string relPath, string abs)
    {
        // Render the real indexed node so the shortcut behaves exactly like the folder in the main tree
        // (expand/collapse, drag/drop, context menu + "Remove Shortcut" via shortcutRelPath).
        var node = FindFolderNode(abs);
        if (node != null)
        {
            DrawFolderNode(node, relPath);
            return;
        }

        // Folder exists on disk but isn't in the index yet — show a minimal fallback with removal.
        ImGui.TextColored(new Num.Vector4(0.7f, 0.7f, 0.7f, 1f), $"[ {Path.GetFileName(abs)} ]  (not indexed)");
        DrawShortcutContextMenu(relPath, abs);
    }

    // Finds the indexed AssetFolderNode for an absolute folder path, or null.
    private AssetFolderNode FindFolderNode(string absolutePath)
    {
        foreach (var root in _db.RootNodes)
        {
            var found = FindFolderNodeRecursive(root, absolutePath);
            if (found != null)
                return found;
        }
        return null;
    }

    private static AssetFolderNode FindFolderNodeRecursive(AssetFolderNode node, string absolutePath)
    {
        if (PathsEqual(node.AbsolutePath, absolutePath))
            return node;

        foreach (var child in node.ChildFolders)
        {
            var found = FindFolderNodeRecursive(child, absolutePath);
            if (found != null)
                return found;
        }
        return null;
    }

    private void DrawFileShortcut(string relPath, string abs)
    {
        var descriptor = AssetTypeRegistry.Resolve(Path.GetExtension(abs));
        var iconId = AssetBrowserIcons.GetIconId(descriptor);
        if (iconId != IntPtr.Zero)
        {
            ImGui.Image(iconId, new Num.Vector2(IconSize, IconSize));
            ImGui.SameLine(0, IconTextSpacing);
        }

        bool clicked = Gui.Selectable(Path.GetFileName(abs), false,
            ImGuiSelectableFlags.AllowDoubleClick, new Num.Vector2(ImGui.GetContentRegionAvail().X, IconSize));

        if (clicked && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
        {
            var reference = _db.GetReference(abs);

            if (descriptor.Open != null)
                descriptor.Open(abs);
            else if (descriptor.Kind == AssetKind.Scene)
                DropHandlers.DropScene(reference);
            else if (descriptor.Kind == AssetKind.Prefab)
                DropHandlers.OpenPrefabIsolated(reference);
            else if (descriptor.Kind == AssetKind.Data)
                Core.GetGlobalManager<ImGuiManager>()?.DataAssetWindow.Open(abs);
        }

        // Drag SOURCE: droppable file shortcuts carry an AssetReference for scene/inspector drops.
        if (ImGui.BeginDragDropSource(ImGuiDragDropFlags.None))
        {
            DraggedSourcePath = abs;
            DraggedSourcePaths.Clear();
            bool isDroppable = descriptor.DropFactory != null;
            DraggedReference = isDroppable ? _db.GetReference(abs) : default;
            unsafe
            {
                byte sentinel = 0;
                ImGui.SetDragDropPayload(DragDropPayloadId, (IntPtr)(&sentinel), 1);
            }
            ImGuiSafe.TextSafe(Path.GetFileName(abs));
            ImGui.EndDragDropSource();
        }

        DrawShortcutContextMenu(relPath, abs);
    }

    private void DrawMissingShortcut(string relPath)
    {
        ImGui.TextColored(new Num.Vector4(0.8f, 0.4f, 0.4f, 1f), $"{Path.GetFileName(relPath)}  (missing)");
        DrawShortcutContextMenu(relPath, null);
    }

    // Right-click menu for a shortcut row — currently just "Remove Shortcut".
    private void DrawShortcutContextMenu(string relPath, string abs)
    {
        if (!Gui.BeginPopupContextItem($"##shortcutctx_{relPath}"))
            return;

        if (Gui.MenuItem("Remove Shortcut"))
            RemoveShortcut(relPath);

        Gui.EndPopup();
    }

    // Adds a folder/file to the Shortcuts section (idempotent) and persists.
    private void AddShortcut(string absolutePath)
    {
        if (string.IsNullOrEmpty(absolutePath))
            return;

        var project = ProjectManager.Instance?.CurrentProject;
        if (project == null)
            return;

        string rel = CrossPlatformPath.GetRelativePathForStorage(project.ProjectPath, absolutePath);
        if (string.IsNullOrEmpty(rel))
            return;

        if (!_shortcuts.Contains(rel, StringComparer.OrdinalIgnoreCase))
        {
            _shortcuts.Add(rel);
            SaveShortcuts();
            EditorDebug.Log($"AssetBrowser: added shortcut '{rel}'.", "AssetBrowser");
        }
    }

    private void RemoveShortcut(string relPath)
    {
        int removed = _shortcuts.RemoveAll(s => string.Equals(s, relPath, StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
            SaveShortcuts();
    }

    // Drops any shortcuts that pointed at (or inside) a folder that was just deleted.
    private void RemoveShortcutsUnder(string deletedFolderAbs)
    {
        var project = ProjectManager.Instance?.CurrentProject;
        if (project == null)
            return;

        int before = _shortcuts.Count;
        _shortcuts.RemoveAll(rel =>
        {
            var abs = ShortcutToAbsolute(rel);
            return abs != null && (PathsEqual(abs, deletedFolderAbs) || IsPathUnder(abs, deletedFolderAbs));
        });
        if (_shortcuts.Count != before)
            SaveShortcuts();
    }

    // Rewrites shortcut paths after a folder rename/move so they keep pointing at the item.
    private void RepointShortcutsAfterMove(string oldAbs, string newAbs)
    {
        var project = ProjectManager.Instance?.CurrentProject;
        if (project == null)
            return;

        bool changed = false;
        for (int i = 0; i < _shortcuts.Count; i++)
        {
            var abs = ShortcutToAbsolute(_shortcuts[i]);
            if (abs == null)
                continue;

            string newTarget = null;
            if (PathsEqual(abs, oldAbs))
                newTarget = newAbs;
            else if (IsPathUnder(abs, oldAbs))
                newTarget = newAbs + abs.Substring(oldAbs.Length); // preserve the tail under the moved folder

            if (newTarget != null)
            {
                _shortcuts[i] = CrossPlatformPath.GetRelativePathForStorage(project.ProjectPath, newTarget);
                changed = true;
            }
        }
        if (changed)
            SaveShortcuts();
    }

    private static string ShortcutToAbsolute(string relPath)
    {
        var project = ProjectManager.Instance?.CurrentProject;
        if (project == null || string.IsNullOrEmpty(relPath))
            return null;
        return Path.GetFullPath(Path.Combine(project.ProjectPath,
            relPath.Replace('/', Path.DirectorySeparatorChar)));
    }

    private void LoadShortcuts()
    {
        _shortcuts.Clear();
        var stored = _shortcutsSetting?.Value;
        if (string.IsNullOrEmpty(stored))
            return;

        foreach (var part in stored.Split(CollapsedNodeSep, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!_shortcuts.Contains(part, StringComparer.OrdinalIgnoreCase))
                _shortcuts.Add(part);
        }
    }

    private void SaveShortcuts()
    {
        _shortcutsSetting.Value = string.Join(CollapsedNodeSep, _shortcuts);
    }
}
