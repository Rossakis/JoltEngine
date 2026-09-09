using System;
using System.Collections.Generic;
using System.IO;
using ImGuiNET;
using Voltage.Editor.Assets;
using Voltage.Editor.Windows;
using EngineAssetReference = Voltage.Serialization.AssetReference;

namespace Voltage.Editor.Tools.Tilemap
{
	/// <summary>Shared asset-slot UI for the tile windows: a drag-droppable button backed by an extension-filtered picker.</summary>
	public static class TileAssetUtils
	{
		public static List<AssetItem> FindAssets(params string[] extensions)
		{
			var results = new List<AssetItem>();
			var db = AssetDatabase.Instance;
			if (db == null)
				return results;

			foreach (var root in db.RootNodes)
				Collect(root, extensions, results);

			results.Sort((a, b) => string.Compare(a.FileName, b.FileName, StringComparison.OrdinalIgnoreCase));
			return results;
		}

		private static void Collect(AssetFolderNode node, string[] extensions, List<AssetItem> results)
		{
			foreach (var file in node.Files)
			{
				foreach (var ext in extensions)
				{
					if (string.Equals(file.Extension, ext, StringComparison.OrdinalIgnoreCase))
					{
						results.Add(file);
						break;
					}
				}
			}

			foreach (var child in node.ChildFolders)
				Collect(child, extensions, results);
		}

		/// <summary>Draws a labelled asset slot; returns true when the reference changed. Right-click clears.</summary>
		public static bool DrawAssetSlot(string label, ref EngineAssetReference reference, string[] extensions)
		{
			var changed = false;
			var id = $"##tileslot_{label}";

			ImGui.TextUnformatted(label);
			ImGui.SameLine(160f);

			var display = reference.IsValid
				? reference.AssetName ?? Path.GetFileName(reference.AssetPath)
				: "(None)";

			ImGui.PushStyleColor(ImGuiCol.Button, new System.Numerics.Vector4(0.25f, 0.30f, 0.38f, 1f));
			if (Gui.Button($"{display}{id}", new System.Numerics.Vector2(-1f, 0f)))
				ImGui.OpenPopup($"pick{id}");
			ImGui.PopStyleColor();

			if (ImGui.BeginDragDropTarget())
			{
				unsafe
				{
					var payload = ImGui.AcceptDragDropPayload(AssetBrowserWindow.DragDropPayloadId);
					if (payload.NativePtr != null)
					{
						var dragged = AssetBrowserWindow.DraggedReference;
						if (!dragged.IsEmpty && MatchesExtension(dragged.HintPath, extensions))
						{
							reference = ToEngineReference(dragged);
							changed = true;
						}
					}
				}

				ImGui.EndDragDropTarget();
			}

			if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Right) && reference.IsValid)
			{
				reference = default;
				changed = true;
			}

			if (ImGui.IsItemHovered())
				ImGui.SetTooltip(reference.IsValid
					? $"{reference.AssetPath}\nRight-click to clear."
					: "Click to pick, or drag an asset here from the Asset Browser.");

			if (Gui.BeginPopup($"pick{id}"))
			{
				if (Gui.MenuItem("(None)"))
				{
					reference = default;
					changed = true;
				}

				ImGui.Separator();

				foreach (var item in FindAssets(extensions))
				{
					if (!Gui.MenuItem(item.FileName))
						continue;

					var db = AssetDatabase.Instance;
					if (db != null)
					{
						reference = ToEngineReference(db.GetReference(item.AbsolutePath));
						changed = true;
					}
				}

				Gui.EndPopup();
			}

			return changed;
		}

		public static EngineAssetReference ToEngineReference(Voltage.Editor.Assets.AssetReference reference) => new()
		{
			AssetGuid = reference.Guid,
			AssetPath = reference.HintPath,
			AssetName = Path.GetFileNameWithoutExtension(reference.HintPath),
		};

		private static bool MatchesExtension(string path, string[] extensions)
		{
			if (string.IsNullOrEmpty(path))
				return false;

			var ext = Path.GetExtension(path);
			foreach (var candidate in extensions)
			{
				if (string.Equals(ext, candidate, StringComparison.OrdinalIgnoreCase))
					return true;
			}

			return false;
		}
	}
}
