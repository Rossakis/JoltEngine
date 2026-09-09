using System;
using System.Collections.Generic;
using System.IO;
using ImGuiNET;
using Voltage.Data;
using Voltage.Editor.DebugUtils;
using Voltage.Editor.Inspectors;
using Voltage.Editor.Inspectors.TypeInspectors;
using Voltage.Editor.Undo.Core;
using Voltage.Editor.Utils;
using Num = System.Numerics;

namespace Voltage.Editor.Windows
{
	/// <summary>
	/// The editor for <b>every</b> <c>.vasset</c> data container — one window, all user-declared types.
	/// </summary>
	public class DataAssetWindow
	{
		public bool IsOpen;

		private DataAsset _asset;
		private string _path;
		private string _typeId;
		private List<AbstractTypeInspector> _inspectors;

		private bool _dirty;
		private int _seenReloadVersion;
		private int _seenValueWrites;
		private bool _closePending;
		private string _pendingOpenPath;

		private const string UnsavedPopupId = "Unsaved Changes##DataAsset";
		private string _status;
		private double _statusClearAt;

		/// <summary>Replaces whatever was open, prompting first when the current asset has unsaved edits.</summary>
		public void Open(string absolutePath)
		{
			if (string.IsNullOrEmpty(absolutePath))
				return;

			// Re-opening the same asset must not reset _dirty while the edits sit in the shared cache instance.
			if (IsEditing(absolutePath))
			{
				IsOpen = true;
				return;
			}

			if (HasUnsavedChanges)
			{
				IsOpen = true;
				RequestClose(absolutePath);
				return;
			}

			OpenAsset(absolutePath);
		}

		private void OpenAsset(string absolutePath)
		{
			var asset = DataAssetCache.GetByPath(absolutePath);
			if (asset == null)
			{
				_status = $"Could not open '{Path.GetFileName(absolutePath)}' — see the console.";
				_statusClearAt = ImGui.GetTime() + 6.0;
				IsOpen = true;
				return;
			}

			_asset = asset;
			_path = absolutePath;
			_typeId = DataAssetRegistry.TryGetId(asset.GetType()) ?? asset.GetType().Name;
			_inspectors = TypeInspectorUtils.GetInspectableProperties(asset);
			_dirty = false;
			_seenReloadVersion = DataAssetCache.ReloadCountFor(_asset);
			_seenValueWrites = AbstractTypeInspector.WriteCountFor(_asset);
			_status = null;
			IsOpen = true;
		}

		/// <summary>Drops the open asset without prompting, e.g. on project close.</summary>
		public void Close()
		{
			EditorChangeTracker.ClearChangesFor(_asset);
			_asset = null;
			_path = null;
			_inspectors = null;
			IsOpen = false;
		}

		/// <summary>True when the open asset has edits that are not on disk yet.</summary>
		public bool HasUnsavedChanges => _dirty && _asset != null && !string.IsNullOrEmpty(_path);

		/// <summary>Name shown in the editor-wide unsaved-changes prompt.</summary>
		public string UnsavedAssetName => _path == null ? null : Path.GetFileName(_path);

		/// <summary>Writes the open asset if it has pending edits. Used by the editor-wide save prompt.</summary>
		public void SaveIfDirty()
		{
			if (HasUnsavedChanges)
				Save();
		}

		/// <summary>Throws the in-memory edits away and re-reads the file, so "Don't Save" really discards.</summary>
		public void DiscardChanges()
		{
			if (_asset == null || string.IsNullOrEmpty(_path))
				return;

			EditorChangeTracker.ClearChangesFor(_asset);
			DataAssetCache.ReloadPath(_path);
			RefreshFromDisk();
		}

		/// <summary>Routes every close - the title-bar X, or switching to another asset - through the prompt.</summary>
		private void RequestClose(string nextPath)
		{
			_pendingOpenPath = nextPath;

			if (!HasUnsavedChanges)
			{
				CompleteClose();
				return;
			}

			_closePending = true;
		}

		private void CompleteClose()
		{
			var next = _pendingOpenPath;
			_pendingOpenPath = null;
			_closePending = false;

			if (next != null)
				OpenAsset(next);
			else
				Close();
		}

		private void DrawUnsavedChangesPrompt()
		{
			if (_closePending && !ImGui.IsPopupOpen(UnsavedPopupId))
				ImGui.OpenPopup(UnsavedPopupId);

			if (!Gui.BeginPopupModal(UnsavedPopupId))
				return;

			ImGuiSafe.TextWrappedSafe($"'{UnsavedAssetName}' has unsaved changes.");
			ImGui.Spacing();

			if (Gui.Button("Save", new Num.Vector2(110, 0)))
			{
				Save();

				// Still dirty means the write failed - keep the prompt open instead of closing over it.
				if (!HasUnsavedChanges)
				{
					CompleteClose();
					ImGui.CloseCurrentPopup();
				}
			}

			ImGui.SameLine();

			if (Gui.Button("Don't Save", new Num.Vector2(110, 0)))
			{
				DiscardChanges();
				CompleteClose();
				ImGui.CloseCurrentPopup();
			}

			ImGui.SameLine();

			if (Gui.Button("Cancel", new Num.Vector2(110, 0)))
			{
				_closePending = false;
				_pendingOpenPath = null;
				IsOpen = true;
				ImGui.CloseCurrentPopup();
			}

			Gui.EndPopup();
		}

		/// <summary>True when that path is the asset currently being edited.</summary>
		public bool IsEditing(string absolutePath) =>
			_path != null && string.Equals(_path, absolutePath, StringComparison.OrdinalIgnoreCase);

		/// <summary>
		/// Rebuilds the inspector list.
		/// </summary>
		public void RefreshFromDisk()
		{
			if (_asset == null)
				return;

			_inspectors = TypeInspectorUtils.GetInspectableProperties(_asset);
			_seenReloadVersion = DataAssetCache.ReloadCountFor(_asset);
			_seenValueWrites = AbstractTypeInspector.WriteCountFor(_asset);
			// The disk copy is now the truth, so the tracker must forget the edits too or the editor stays dirty forever.
			EditorChangeTracker.ClearChangesFor(_asset);
			_dirty = false;
		}

		public void Draw()
		{
			if (IsOpen)
				DrawWindow();

			// Outside the window on purpose: Begin returns false for a collapsed window or hidden tab, and the prompt must stay answerable.
			DrawUnsavedChangesPrompt();
		}

		private void DrawWindow()
		{
			ImGui.SetNextWindowSize(new Num.Vector2(460, 520), ImGuiCond.FirstUseEver);

			var open = IsOpen;
			var beginResult = Gui.Begin("Data Asset", ref open, ImGuiWindowFlags.MenuBar);

			// The title-bar X routes through the same prompt as the menu item.
			if (!open && IsOpen)
				RequestClose(null);
			else
				IsOpen = open;

			if (!beginResult)
			{
				Gui.End();
				return;
			}

			DrawMenuBar();

			if (_asset == null)
			{
				ImGuiSafe.TextColoredSafe(new Num.Vector4(0.6f, 0.6f, 0.6f, 1f),
					"No data asset open.\n\nDouble-click a .vasset in the Asset Browser, or create one with\n" +
					"right-click ▸ Create ▸ Data Asset.");
				DrawStatus();
				Gui.End();
				return;
			}

			if (_seenReloadVersion != DataAssetCache.ReloadCountFor(_asset))
			{
				RefreshFromDisk();
				SetStatus("Reloaded — the file changed on disk.");
			}

			DrawHeader();
			ImGui.Separator();

			if (_inspectors.Count == 0)
			{
				ImGuiSafe.TextColoredSafe(new Num.Vector4(1f, 0.7f, 0.3f, 1f),
					"This type has no public fields, so there is nothing to edit.");
			}
			else
			{
				foreach (var inspector in _inspectors)
					inspector.Draw();
			}

			// Only a real write counts; IsAnyItemActive treated expanding a header as an edit.
			var writes = AbstractTypeInspector.WriteCountFor(_asset);
			if (writes != _seenValueWrites)
			{
				_seenValueWrites = writes;
				_dirty = true;

				// Tracked editor-wide too, so the exit prompt lists it like an unsaved scene.
				EditorChangeTracker.MarkChanged(_asset, $"Data asset '{Path.GetFileNameWithoutExtension(_path)}'");
			}


			if (ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) &&
				(ImGui.GetIO().KeySuper || ImGui.GetIO().KeyCtrl) && ImGui.IsKeyPressed(ImGuiKey.S))
			{
				Save();
			}

			DrawStatus();
			Gui.End();
		}

		private void DrawMenuBar()
		{
			if (!ImGui.BeginMenuBar())
				return;

			if (Gui.MenuItem("Save", "Cmd/Ctrl+S", false, _asset != null))
				Save();

			if (Gui.MenuItem("Reveal", null, false, _path != null))
				AssetBrowserWindow.PingAsset(_path);

			if (Gui.MenuItem("Reload", null, false, _path != null))
			{
				DataAssetCache.ReloadPath(_path);
				RefreshFromDisk();
				SetStatus("Reloaded from disk.");
			}

			ImGui.EndMenuBar();
		}

		private void DrawHeader()
		{
			var grey = new Num.Vector4(0.6f, 0.6f, 0.6f, 1f);

			ImGuiSafe.TextColoredSafe(new Num.Vector4(0.3f, 0.8f, 1f, 1f), Path.GetFileNameWithoutExtension(_path));

			ImGui.SameLine();
			ImGuiSafe.TextColoredSafe(grey, $"({_asset.GetType().Name})");

			if (ImGui.IsItemHovered())
			{
				ImGui.BeginTooltip();
				ImGuiSafe.TextSafe($"Asset type id: {_typeId}");
				ImGuiSafe.TextSafe($"CLR type:      {_asset.GetType().FullName}");
				ImGuiSafe.TextSafe($"GUID:          {_asset.SourceGuid}");
				ImGuiSafe.TextSafe($"Path:          {_path}");
				ImGuiSafe.TextSafe(string.Empty);
				ImGuiSafe.TextSafe("The id — not the class name — is what .vasset files store,");
				ImGuiSafe.TextSafe("so renaming the class keeps every reference working.");
				ImGui.EndTooltip();
			}

			ImGuiSafe.TextColoredSafe(grey, "Shared instance — edits apply everywhere this asset is used.");
		}

		private void DrawStatus()
		{
			if (_status == null)
				return;

			if (ImGui.GetTime() > _statusClearAt)
			{
				_status = null;
				return;
			}

			ImGui.Separator();
			ImGuiSafe.TextColoredSafe(new Num.Vector4(0.5f, 0.9f, 0.5f, 1f), _status);
		}

		private void SetStatus(string message)
		{
			_status = message;
			_statusClearAt = ImGui.GetTime() + 3.0;
		}

		private void Save()
		{
			if (_asset == null || string.IsNullOrEmpty(_path))
			{
				_dirty = false;
				return;
			}

			try
			{
				DataAssetIO.Save(_asset, _path);

				// Cleared after the write lands, so a failed save cannot let the close prompt discard the edits.
				_dirty = false;
				EditorChangeTracker.ClearChangesFor(_asset);
				SetStatus($"Saved {Path.GetFileName(_path)}.");
			}
			catch (Exception ex)
			{
				EditorDebug.Log($"DataAssetWindow: failed to save '{_path}': {ex.Message}", "DataAsset");
				_status = $"Save failed: {ex.Message}";
				_statusClearAt = ImGui.GetTime() + 8.0;
			}
		}
	}
}
