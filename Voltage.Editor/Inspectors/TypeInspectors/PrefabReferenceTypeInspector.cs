using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ImGuiNET;
using Voltage.Editor.Assets;
using Voltage.Editor.ProjectFile;
using Voltage.Editor.Utils;
using Voltage.Editor.Windows;
using Voltage.Serialization;
using Voltage.Utils;
using Num = System.Numerics;

namespace Voltage.Editor.Inspectors.TypeInspectors;

public class PrefabReferenceTypeInspector : AbstractTypeInspector
{
    private bool _showPicker;
    private string _pickerSearch = string.Empty;

    private readonly record struct PrefabEntry(string AbsolutePath, string Name, string SubFolder);

    public override void DrawMutable()
    {
        var current = (PrefabReference)_getter(_target);

        ImGuiSafe.TextSafe(_name);
        ImGui.SameLine();

        var label = current.IsValid
            ? current.PrefabName ?? current.PrefabPath
            : $"None (PrefabReference)";

        var buttonColor = current.IsValid
            ? new Num.Vector4(0.45f, 0.25f, 0.55f, 0.9f)
            : new Num.Vector4(0.22f, 0.22f, 0.22f, 0.9f);

        ImGui.PushStyleColor(ImGuiCol.Button, buttonColor);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, buttonColor with { W = 1f });

        // Single click reveals the prefab in the Asset Browser; double click opens the picker.
        var pressed = Gui.Button($"{label}##prefabref_{_scopeId}", new Num.Vector2(-1, 0));
        var doubleClicked = ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left);

        if (doubleClicked || (pressed && !current.IsValid))
            _showPicker = true;
        else if (pressed)
            AssetBrowserWindow.PingAsset(ResolvePrefabPath(current));

        ImGui.PopStyleColor(2);

        if (current.IsValid && ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGuiSafe.TextSafe($"Path: {current.PrefabPath}");
            ImGuiSafe.TextSafe($"GUID: {current.PrefabGuid}");
            ImGuiSafe.TextSafe("Click to reveal in the Asset Browser — double-click to change.");
            ImGui.EndTooltip();
        }
        else
        {
            HandleTooltip();
        }

        if (current.IsValid && Gui.BeginPopupContextItem($"prefabref_ctx_{_scopeId}"))
        {
            if (Gui.Selectable("Clear"))
                SetValueWithUndo(default(PrefabReference), $"Clear {_name}");

            Gui.EndPopup();
        }

        if (_showPicker)
        {
            ImGui.OpenPopup($"prefabref_picker_{_scopeId}");
            _pickerSearch = string.Empty;
            _showPicker = false;
        }

        DrawPickerPopup(current);
    }

    /// <summary>Resolves the prefab's file, GUID first so a renamed or moved prefab still resolves.</summary>
    private static string ResolvePrefabPath(PrefabReference reference)
    {
        var db = AssetDatabase.Instance;

        if (reference.PrefabGuid != Guid.Empty && db != null)
        {
            var byGuid = db.GetPath(reference.PrefabGuid);
            if (!string.IsNullOrEmpty(byGuid))
                return byGuid;
        }

        if (string.IsNullOrEmpty(reference.PrefabPath))
            return null;

        if (Path.IsPathRooted(reference.PrefabPath))
            return reference.PrefabPath;

        var project = ProjectManager.Instance?.CurrentProject;
        return project == null
            ? reference.PrefabPath
            : Path.Combine(project.ProjectPath, reference.PrefabPath);
    }

    private void DrawPickerPopup(PrefabReference current)
    {
        var center = new Num.Vector2(Screen.Width * 0.5f, Screen.Height * 0.5f);
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Num.Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Num.Vector2(420, 460), ImGuiCond.Appearing);

        bool open = true;
        if (!Gui.BeginPopupModal($"prefabref_picker_{_scopeId}", ref open, ImGuiWindowFlags.NoResize))
            return;

        ImGuiSafe.TextColoredSafe(new Num.Vector4(0.8f, 0.5f, 1f, 1f), $"{_name}  (PrefabReference)");
        ImGui.Separator();

        ImGui.SetNextItemWidth(-1);
        Gui.InputTextWithHint("##prefabsearch", "Search...", ref _pickerSearch, 128);
        ImGui.Separator();

        if (Gui.Selectable("  None (PrefabReference)", !current.IsValid))
        {
            SetValueWithUndo(default(PrefabReference), $"Clear {_name}");
            ImGui.CloseCurrentPopup();
        }

        ImGui.Separator();

        var entries = CollectPrefabEntries();
        string lower = _pickerSearch.ToLowerInvariant();
        bool filtering = !string.IsNullOrEmpty(_pickerSearch);

        string lastFolder = null;
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (filtering && !entry.Name.ToLowerInvariant().Contains(lower))
                continue;

            if (!filtering && entry.SubFolder != lastFolder)
            {
                if (lastFolder != null)
                    ImGui.Separator();
                ImGuiSafe.TextDisabledSafe(entry.SubFolder ?? "Prefabs");
                lastFolder = entry.SubFolder;
            }

            bool isCurrent = current.IsValid &&
                string.Equals(current.PrefabName, entry.Name, StringComparison.OrdinalIgnoreCase);

            ImGui.PushID(i);
            if (Gui.Selectable($"  {entry.Name}", isCurrent))
            {
                AssignPrefab(entry);
                ImGui.CloseCurrentPopup();
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGuiSafe.TextSafe(entry.AbsolutePath);
                ImGui.EndTooltip();
            }

            ImGui.PopID();
        }

        ImGui.Separator();
        VoltageEditorUtils.SmallVerticalSpace();
        if (VoltageEditorUtils.CenteredButton("Cancel", 0.5f))
            ImGui.CloseCurrentPopup();

        Gui.EndPopup();
    }

    private void AssignPrefab(PrefabEntry entry)
    {
        var assetRef = AssetDatabase.Instance != null
            ? AssetDatabase.Instance.GetReference(entry.AbsolutePath)
            : Voltage.Editor.Assets.AssetReference.Empty;

        var projectRoot = ProjectManager.Instance?.CurrentProject?.ProjectPath ?? string.Empty;
        string relPath = MakeProjectRelative(entry.AbsolutePath, projectRoot);

        var prefabRef = new PrefabReference
        {
            PrefabGuid = assetRef.Guid,
            PrefabPath = relPath,
            PrefabName = entry.Name,
        };

        SetValueWithUndo(prefabRef, $"Assign {_name} = {entry.Name}");
    }

    private static List<PrefabEntry> CollectPrefabEntries()
    {
        var result = new List<PrefabEntry>();
        var prefabsDir = ProjectManager.Instance?.CurrentProject?.PrefabsFolder;
        if (string.IsNullOrEmpty(prefabsDir) || !Directory.Exists(prefabsDir))
            return result;

        // Mirror SceneGraphWindow.RefreshPrefabCache: subdirectory → files
        var subDirs = Directory.GetDirectories(prefabsDir);
        foreach (var subDir in subDirs)
        {
            string folderName = Path.GetFileName(subDir);
            var files = Directory.GetFiles(subDir, "*.vprefab")
                .Concat(Directory.GetFiles(subDir, "*.prefab"));
            foreach (var file in files)
                result.Add(new PrefabEntry(file, Path.GetFileNameWithoutExtension(file), folderName));
        }

        // Also scan the root prefabs folder directly (flat layout)
        var rootFiles = Directory.GetFiles(prefabsDir, "*.vprefab")
            .Concat(Directory.GetFiles(prefabsDir, "*.prefab"));
        foreach (var file in rootFiles)
            result.Add(new PrefabEntry(file, Path.GetFileNameWithoutExtension(file), null));

        return result;
    }

    // Shared entry-point for ListInspector
    internal static List<(string AbsolutePath, string Name, string SubFolder)> CollectPrefabEntriesInternal()
    {
        var raw = CollectPrefabEntries();
        var result = new List<(string, string, string)>(raw.Count);
        foreach (var e in raw)
            result.Add((e.AbsolutePath, e.Name, e.SubFolder));
        return result;
    }

    // Builds a PrefabReference from an absolute path + name. Used by ListInspector
    internal static PrefabReference BuildPrefabRef(string absolutePath, string name)
    {
        var assetRef   = AssetDatabase.Instance?.GetReference(absolutePath) ?? Assets.AssetReference.Empty;
        var projectRoot = ProjectManager.Instance?.CurrentProject?.ProjectPath ?? string.Empty;
        string relPath  = MakeProjectRelative(absolutePath, projectRoot);
        return new PrefabReference
        {
            PrefabGuid = assetRef.Guid,
            PrefabPath = relPath,
            PrefabName = name,
        };
    }

    private static string MakeProjectRelative(string absolutePath, string projectRoot)
    {
        if (string.IsNullOrEmpty(projectRoot))
            return absolutePath;

        string root = projectRoot.Replace('\\', '/').TrimEnd('/') + '/';
        string abs  = absolutePath.Replace('\\', '/');
        return abs.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? abs[root.Length..]
            : abs;
    }
}
