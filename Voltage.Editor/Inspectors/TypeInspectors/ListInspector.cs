using System;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using ImGuiNET;
using Microsoft.Xna.Framework;
using Voltage;
using Voltage.Editor.Utils;
using Voltage.Editor.Windows;
using Voltage.Serialization;
using Voltage.Utils;
using Num = System.Numerics;


namespace Voltage.Editor.Inspectors.TypeInspectors
{
	public class ListInspector : AbstractTypeInspector
	{
		public static Type[] KSupportedTypes =
			{typeof(int), typeof(uint), typeof(long), typeof(ulong), typeof(float), typeof(string), typeof(Vector2)};

		enum ElementKind { Primitive, Entity, Transform, Component, AssetRef, PrefabRef }

		IList _list;
		Type _elementType;
		bool _isArray;
		ElementKind _elementKind;

		// Which list index is waiting for a picker popup this frame
		int _pendingPickerIndex = -1;
		int _activePickerIndex = -1;
		string _pickerSearch = string.Empty;


		public override void Initialize()
		{
			base.Initialize();

			_isArray = _valueType.IsArray;
			_elementType = _isArray ? _valueType.GetElementType() : _valueType.GetGenericArguments()[0];

			if (_elementType == typeof(Entity))
				_elementKind = ElementKind.Entity;
			else if (_elementType == typeof(Transform))
				_elementKind = ElementKind.Transform;
			else if (typeof(Component).IsAssignableFrom(_elementType))
				_elementKind = ElementKind.Component;
			else if (_elementType == typeof(AssetReference))
				_elementKind = ElementKind.AssetRef;
			else if (_elementType == typeof(PrefabReference))
				_elementKind = ElementKind.PrefabRef;
			else
				_elementKind = ElementKind.Primitive;

			RefreshList();
		}

		public override void DrawMutable()
		{
			// Re-fetch in case the backing field was replaced (e.g. array resize)
			RefreshList();

			ImGui.Indent();
			if (ImGui.CollapsingHeader($"{_name} [{_list.Count}]###{_name}", ImGuiTreeNodeFlags.FramePadding))
			{
				ImGui.Indent();

				if (_isArray)
				{
					// Array size control — arrays must be replaced to resize
					int size = _list.Count;
					ImGui.SetNextItemWidth(100);
					if (ImGui.InputInt($"Size##arrsize_{_scopeId}", ref size, 1) && size >= 0 && size != _list.Count)
						ResizeArray(size);
				}
				else
				{
					// List: Add / Clear
					if (ImGui.Button("Add Element"))
					{
						if (_elementKind == ElementKind.AssetRef)
							_list.Add(default(Voltage.Serialization.AssetReference));
						else if (_elementKind == ElementKind.PrefabRef)
							_list.Add(default(PrefabReference));
						else if (_elementKind != ElementKind.Primitive)
							_list.Add(null);
						else if (_elementType == typeof(string))
							_list.Add("");
						else
							_list.Add(Activator.CreateInstance(_elementType));

						NoteValueWritten();
					}

					ImGui.SameLine(ImGui.GetWindowWidth() - ImGui.GetItemRectSize().X -
					               ImGui.GetStyle().ItemInnerSpacing.X);

					if (ImGui.Button("Clear"))
						ImGui.OpenPopup("Clear Data");

					if (VoltageEditorUtils.SimpleDialog("Clear Data", "Are you sure you want to clear the data?"))
					{
						_list.Clear();
						NoteValueWritten();
						Debug.Log($"list count: {_list.Count}");
					}
				}

				ImGui.PushItemWidth(-ImGui.GetStyle().IndentSpacing);

				int removeAt = -1;
				for (var i = 0; i < _list.Count; i++)
				{
					if (_elementKind == ElementKind.Primitive)
						DrawPrimitiveSlot(i);
					else if (_elementKind == ElementKind.AssetRef)
						DrawAssetReferenceSlot(i, ref removeAt);
					else if (_elementKind == ElementKind.PrefabRef)
						DrawPrefabReferenceSlot(i, ref removeAt);
					else
						DrawReferenceSlot(i, ref removeAt);
				}

				if (removeAt >= 0 && !_isArray)
				{
					_list.RemoveAt(removeAt);
					NoteValueWritten();
				}

				ImGui.PopItemWidth();

				// Draw the single shared picker popup — one per ListInspector per frame
				if (_elementKind == ElementKind.Entity || _elementKind == ElementKind.Transform)
					DrawEntityPickerPopup();
				else if (_elementKind == ElementKind.Component)
					DrawComponentPickerPopup();
				else if (_elementKind == ElementKind.AssetRef)
					DrawAssetReferencePickerPopup();
				else if (_elementKind == ElementKind.PrefabRef)
					DrawPrefabRefPickerPopup();

				ImGui.Unindent();
			}

			ImGui.Unindent();
		}


		void DrawPrimitiveSlot(int i)
		{
			if (_elementType == typeof(int))
				DrawWidget((int) Convert.ChangeType(_list[i], _elementType), i);
			else if (_elementType == typeof(float))
				DrawWidget((float) Convert.ChangeType(_list[i], _elementType), i);
			else if (_elementType == typeof(string))
				DrawWidget((string) Convert.ChangeType(_list[i], _elementType), i);
			else if (_elementType == typeof(Vector2))
				DrawWidget((Vector2) Convert.ChangeType(_list[i], _elementType), i);
		}

		void DrawWidget(int value, int index)
		{
			if (ImGui.DragInt($"{index}", ref value))
			{
				_list[index] = value;
				NoteValueWritten();
				SpecialCasesHandling();
			}
		}

		void DrawWidget(float value, int index)
		{
			if (ImGui.DragFloat($"{index}", ref value))
			{
				_list[index] = value;
				NoteValueWritten();
				SpecialCasesHandling();
			}
		}

		void DrawWidget(string value, int index)
		{
			if (ImGui.InputText($"{index}", ref value, 200))
			{
				_list[index] = value;
				NoteValueWritten();
				SpecialCasesHandling();
			}
		}

		void DrawWidget(Vector2 value, int index)
		{
			var vec = value.ToNumerics();
			if (ImGui.DragFloat2($"{index}", ref vec))
			{
				_list[index] = vec.ToXNA();
				NoteValueWritten();
				SpecialCasesHandling();
			}
		}

		// Reference element drawing (Entity / Transform / Component)

		void DrawReferenceSlot(int index, ref int removeAt)
		{
			ImGui.PushID(index);

			// Build label and colour based on what's currently assigned
			string label;
			Num.Vector4 buttonColor;

			if (_elementKind == ElementKind.Component)
			{
				var comp = _list[index] as Component;
				label = comp != null ? comp.ToString() : $"None ({_elementType.Name})";
				buttonColor = comp != null
					? new Num.Vector4(0.2f, 0.35f, 0.55f, 0.9f)
					: new Num.Vector4(0.22f, 0.22f, 0.22f, 0.9f);
			}
			else
			{
				var entity = ResolveEntityAt(index);
				label = entity != null ? entity.Name : $"None ({_elementType.Name})";
				buttonColor = entity != null
					? new Num.Vector4(0.2f, 0.45f, 0.35f, 0.9f)
					: new Num.Vector4(0.22f, 0.22f, 0.22f, 0.9f);
			}

			// Row Visual: [index]  [reference button -------------] [X]
			ImGui.AlignTextToFramePadding();
			ImGuiSafe.TextSafe($"{index}");
			ImGui.SameLine();

			float removeButtonWidth = !_isArray ? 26f : 0f;
			float spacing = !_isArray ? ImGui.GetStyle().ItemSpacing.X : 0f;
			float availableWidth = ImGui.GetContentRegionAvail().X - removeButtonWidth - spacing;

			ImGui.PushStyleColor(ImGuiCol.Button, buttonColor);
			ImGui.PushStyleColor(ImGuiCol.ButtonHovered, buttonColor with { W = 1f });

			if (ImGui.Button($"{label}##ref_{index}", new Num.Vector2(availableWidth, 0)))
				_pendingPickerIndex = index;

			ImGui.PopStyleColor(2);

			// Drag-drop target on the button
			if (ImGui.BeginDragDropTarget())
			{
				bool accepted = false;

				if (_elementKind == ElementKind.Component)
				{
					var payload = ImGui.AcceptDragDropPayload(ComponentReferenceTypeInspector.DragDropPayloadId);
					unsafe { accepted = payload.NativePtr != null; }
					if (accepted && ComponentReferenceTypeInspector.DraggedComponent != null)
					{
						var dragged = ComponentReferenceTypeInspector.DraggedComponent;
						if (_elementType.IsAssignableFrom(dragged.GetType()))
						{
							_list[index] = dragged;
							NoteValueWritten();
						}
						else
							Debug.Warn($"[ListInspector] Cannot assign {dragged.GetType().Name} to element of type {_elementType.Name}.");
						ComponentReferenceTypeInspector.DraggedComponent = null;
					}
				}
				else
				{
					var payload = ImGui.AcceptDragDropPayload(EntityReferenceTypeInspector.DragDropPayloadId);
					unsafe { accepted = payload.NativePtr != null; }
					if (accepted && EntityReferenceTypeInspector.DraggedEntity != null)
					{
						AssignEntityAt(index, EntityReferenceTypeInspector.DraggedEntity);
						EntityReferenceTypeInspector.DraggedEntity = null;
					}
				}

				ImGui.EndDragDropTarget();
			}

			// Right-click: clear slot
			if (ImGui.BeginPopupContextItem($"reflist_ctx_{index}"))
			{
				if (ImGui.Selectable("Clear"))
				{
					_list[index] = null;
					NoteValueWritten();
				}
				ImGui.EndPopup();
			}

			// Remove button (lists only)
			if (!_isArray)
			{
				ImGui.SameLine();
				if (ImGui.Button($"X##rem_{index}", new Num.Vector2(removeButtonWidth, 0)))
					removeAt = index;
			}

			ImGui.PopID();
		}

		void DrawEntityPickerPopup()
		{
			if (_pendingPickerIndex >= 0)
			{
				ImGui.OpenPopup($"reflist_epicker_{_scopeId}");
				_activePickerIndex = _pendingPickerIndex;
				_pendingPickerIndex = -1;
				_pickerSearch = string.Empty;
			}

			if (_activePickerIndex < 0 || _activePickerIndex >= _list.Count)
				return;

			var center = new Num.Vector2(Screen.Width * 0.5f, Screen.Height * 0.5f);
			ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Num.Vector2(0.5f, 0.5f));
			ImGui.SetNextWindowSize(new Num.Vector2(400, 450), ImGuiCond.Appearing);

			bool open = true;
			if (!ImGui.BeginPopupModal($"reflist_epicker_{_scopeId}", ref open, ImGuiWindowFlags.NoResize))
				return;

			var currentEntity = ResolveEntityAt(_activePickerIndex);

			// Shared hierarchy picker — identical search + scene-tree UI as single Entity/Transform fields.
			if (EntityHierarchyPicker.DrawBody($"{_name}[{_activePickerIndex}]", _elementType, currentEntity,
				    ref _pickerSearch, out bool cleared, out var picked))
			{
				if (cleared)
				{
					_list[_activePickerIndex] = null;
					NoteValueWritten();
				}
				else
					AssignEntityAt(_activePickerIndex, picked);

				_activePickerIndex = -1;
				ImGui.CloseCurrentPopup();
			}

			ImGui.Separator();
			VoltageEditorUtils.SmallVerticalSpace();
			if (VoltageEditorUtils.CenteredButton("Cancel", 0.5f))
			{
				_activePickerIndex = -1;
				ImGui.CloseCurrentPopup();
			}

			ImGui.EndPopup();
		}

		void DrawComponentPickerPopup()
		{
			if (_pendingPickerIndex >= 0)
			{
				ImGui.OpenPopup($"reflist_cpicker_{_scopeId}");
				_activePickerIndex = _pendingPickerIndex;
				_pendingPickerIndex = -1;
			}

			if (_activePickerIndex < 0 || _activePickerIndex >= _list.Count)
				return;

			var center = new Num.Vector2(Screen.Width * 0.5f, Screen.Height * 0.5f);
			ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Num.Vector2(0.5f, 0.5f));
			ImGui.SetNextWindowSize(new Num.Vector2(400, 450), ImGuiCond.Appearing);

			bool open = true;
			if (!ImGui.BeginPopupModal($"reflist_cpicker_{_scopeId}", ref open, ImGuiWindowFlags.NoResize))
				return;

			ImGuiSafe.TextColoredSafe(new Num.Vector4(0.3f, 0.8f, 1f, 1f),
				$"{_name}[{_activePickerIndex}]  ({_elementType.Name})");
			ImGui.Separator();

			var currentComp = _list[_activePickerIndex] as Component;

			if (ImGui.Selectable($"  None ({_elementType.Name})", currentComp == null))
			{
				_list[_activePickerIndex] = null;
				NoteValueWritten();
				_activePickerIndex = -1;
				ImGui.CloseCurrentPopup();
			}

			ImGui.Separator();

			var scene = Core.Scene;
			if (scene != null)
			{
				for (var e = 0; e < scene.Entities.Count; e++)
				{
					var entity = scene.Entities[e];
					for (var c = 0; c < entity.Components.Count; c++)
					{
						var comp = entity.Components[c];
						if (!_elementType.IsAssignableFrom(comp.GetType()))
							continue;

						bool isSelected = comp == currentComp;
						if (ImGui.Selectable($"  {entity.Name}  /  {comp}", isSelected))
						{
							_list[_activePickerIndex] = comp;
							_activePickerIndex = -1;
							ImGui.CloseCurrentPopup();
						}
					}
				}
			}

			ImGui.Separator();
			VoltageEditorUtils.SmallVerticalSpace();
			if (VoltageEditorUtils.CenteredButton("Cancel", 0.5f))
			{
				_activePickerIndex = -1;
				ImGui.CloseCurrentPopup();
			}

			ImGui.EndPopup();
		}

		private AssetSlotFilter _assetFilter;
		private AssetSlotFilter AssetFilter => _assetFilter ??= AssetSlotFilter.For(MemberInfo);

		void DrawAssetReferenceSlot(int index, ref int removeAt)
		{
			ImGui.PushID(index);

			var current = (Voltage.Serialization.AssetReference)_list[index];
			var label   = current.IsValid ? (current.AssetName ?? current.AssetPath) : $"None ({AssetFilter.DisplayTypeName})";
			var btnColor = current.IsValid
				? new Num.Vector4(0.25f, 0.45f, 0.55f, 0.9f)
				: new Num.Vector4(0.22f, 0.22f, 0.22f, 0.9f);

			ImGui.AlignTextToFramePadding();
			ImGuiSafe.TextSafe($"{index}");
			ImGui.SameLine();

			float removeButtonWidth = !_isArray ? 26f : 0f;
			float spacing           = !_isArray ? ImGui.GetStyle().ItemSpacing.X : 0f;
			float availableWidth    = ImGui.GetContentRegionAvail().X - removeButtonWidth - spacing;

			ImGui.PushStyleColor(ImGuiCol.Button, btnColor);
			ImGui.PushStyleColor(ImGuiCol.ButtonHovered, btnColor with { W = 1f });

			if (ImGui.Button($"{label}##asref_{index}", new Num.Vector2(availableWidth, 0)))
				_pendingPickerIndex = index;

			ImGui.PopStyleColor(2);

			if (current.IsValid && ImGui.IsItemHovered())
			{
				ImGui.BeginTooltip();
				ImGuiSafe.TextSafe($"Path: {current.AssetPath}");
				ImGuiSafe.TextSafe($"GUID: {current.AssetGuid}");
				ImGui.EndTooltip();
			}

			if (ImGui.BeginDragDropTarget())
			{
				var dragged = AssetBrowserWindow.DraggedReference;
				var draggedOk = dragged.IsEmpty || !AssetFilter.IsConstrained ||
								AssetFilter.Accepts(Assets.AssetDatabase.Instance?.Resolve(dragged), dragged.Guid);

				var payload = draggedOk
					? ImGui.AcceptDragDropPayload(AssetBrowserWindow.DragDropPayloadId)
					: default;
				bool accepted;
				unsafe { accepted = payload.NativePtr != null; }
				if (accepted && !AssetBrowserWindow.DraggedReference.IsEmpty)
					_list[index] = AssetReferenceTypeInspector.BuildFromDrag();
				ImGui.EndDragDropTarget();
			}

			// Right-click: clear
			if (ImGui.BeginPopupContextItem($"asreflist_ctx_{index}"))
			{
				if (ImGui.Selectable("Clear"))
					_list[index] = default(Voltage.Serialization.AssetReference);
				ImGui.EndPopup();
			}

			if (!_isArray)
			{
				ImGui.SameLine();
				if (ImGui.Button($"X##asrefrem_{index}", new Num.Vector2(removeButtonWidth, 0)))
					removeAt = index;
			}

			ImGui.PopID();
		}

		void DrawAssetReferencePickerPopup()
		{
			if (_pendingPickerIndex >= 0)
			{
				ImGui.OpenPopup($"reflist_aspicker_{_scopeId}");
				_activePickerIndex  = _pendingPickerIndex;
				_pendingPickerIndex = -1;
				_pickerSearch       = string.Empty;
			}

			if (_activePickerIndex < 0 || _activePickerIndex >= _list.Count)
				return;

			var center = new Num.Vector2(Screen.Width * 0.5f, Screen.Height * 0.5f);
			ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Num.Vector2(0.5f, 0.5f));
			ImGui.SetNextWindowSize(new Num.Vector2(420, 460), ImGuiCond.Appearing);

			bool open = true;
			if (!ImGui.BeginPopupModal($"reflist_aspicker_{_scopeId}", ref open, ImGuiWindowFlags.NoResize))
				return;

			var current = (AssetReference)_list[_activePickerIndex];
			ImGuiSafe.TextColoredSafe(new Num.Vector4(0.3f, 0.8f, 1f, 1f), $"{_name}[{_activePickerIndex}]  (AssetReference)");
			ImGui.Separator();

			ImGui.SetNextItemWidth(-1);
			ImGui.InputTextWithHint("##asreflistsearch", "Search...", ref _pickerSearch, 128);
			ImGui.Separator();

			if (ImGui.Selectable("  None (AssetReference)", !current.IsValid))
			{
				_list[_activePickerIndex] = default(AssetReference);
				_activePickerIndex = -1;
				ImGui.CloseCurrentPopup();
			}

			ImGui.Separator();

			var db = Assets.AssetDatabase.Instance;
			if (db != null)
			{
				string lower    = _pickerSearch.ToLowerInvariant();
				bool filtering  = !string.IsNullOrEmpty(_pickerSearch);
				DrawAssetPickerNodes(db.RootNodes, current, lower, filtering);
			}

			ImGui.Separator();
			VoltageEditorUtils.SmallVerticalSpace();
			if (VoltageEditorUtils.CenteredButton("Cancel", 0.5f))
			{
				_activePickerIndex = -1;
				ImGui.CloseCurrentPopup();
			}

			ImGui.EndPopup();
		}

		void DrawAssetPickerNodes(
			IReadOnlyList<Assets.AssetFolderNode> nodes,
			AssetReference current,
			string lower,
			bool filtering)
		{
			foreach (var folder in nodes)
			{
				if (filtering)
				{
					foreach (var item in folder.Files)
						if (item.FileName.ToLowerInvariant().Contains(lower))
							DrawAssetPickerItem(item, current);
					DrawAssetPickerNodes(folder.ChildFolders, current, lower, filtering);
					continue;
				}

				if (!FolderHasAcceptableFiles(folder)) continue;

				if (ImGui.TreeNodeEx(folder.Label, ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.SpanAvailWidth))
				{
					foreach (var item in folder.Files)
						DrawAssetPickerItem(item, current);
					DrawAssetPickerNodes(folder.ChildFolders, current, lower, filtering);
					ImGui.TreePop();
				}
			}
		}

		bool FolderHasAcceptableFiles(Assets.AssetFolderNode folder)
		{
			if (!AssetFilter.IsConstrained)
				return AssetReferenceTypeInspector.FolderHasFilesInternal(folder);

			var db = Assets.AssetDatabase.Instance;
			foreach (var file in folder.Files)
			{
				var guid = db?.GetReference(file.AbsolutePath).Guid ?? Guid.Empty;
				if (AssetFilter.Accepts(file.AbsolutePath, guid))
					return true;
			}

			foreach (var child in folder.ChildFolders)
				if (FolderHasAcceptableFiles(child))
					return true;

			return false;
		}

		void DrawAssetPickerItem(Assets.AssetItem item, AssetReference current)
		{
			var db    = Assets.AssetDatabase.Instance;
			var edRef = db?.GetReference(item.AbsolutePath) ?? Assets.AssetReference.Empty;

			if (AssetFilter.IsConstrained && !AssetFilter.Accepts(item.AbsolutePath, edRef.Guid))
				return;
			bool isCurrent = current.IsValid && current.AssetGuid != Guid.Empty && current.AssetGuid == edRef.Guid;

			if (ImGui.Selectable($"  {item.FileName}", isCurrent))
			{
				_list[_activePickerIndex] = new AssetReference
				{
					AssetGuid = edRef.Guid,
					AssetPath = edRef.HintPath,
					AssetName = Path.GetFileNameWithoutExtension(item.FileName),
				};
				_activePickerIndex = -1;
				ImGui.CloseCurrentPopup();
			}

			if (ImGui.IsItemHovered())
			{
				ImGui.BeginTooltip();
				ImGuiSafe.TextSafe(item.AbsolutePath);
				ImGui.EndTooltip();
			}
		}

		void DrawPrefabReferenceSlot(int index, ref int removeAt)
		{
			ImGui.PushID(index);

			var current  = (PrefabReference)_list[index];
			var label    = current.IsValid ? (current.PrefabName ?? current.PrefabPath) : "None (PrefabReference)";
			var btnColor = current.IsValid
				? new Num.Vector4(0.45f, 0.25f, 0.55f, 0.9f)
				: new Num.Vector4(0.22f, 0.22f, 0.22f, 0.9f);

			ImGui.AlignTextToFramePadding();
			ImGuiSafe.TextSafe($"{index}");
			ImGui.SameLine();

			float removeButtonWidth = !_isArray ? 26f : 0f;
			float spacing           = !_isArray ? ImGui.GetStyle().ItemSpacing.X : 0f;
			float availableWidth    = ImGui.GetContentRegionAvail().X - removeButtonWidth - spacing;

			ImGui.PushStyleColor(ImGuiCol.Button, btnColor);
			ImGui.PushStyleColor(ImGuiCol.ButtonHovered, btnColor with { W = 1f });

			if (ImGui.Button($"{label}##prref_{index}", new Num.Vector2(availableWidth, 0)))
				_pendingPickerIndex = index;

			ImGui.PopStyleColor(2);

			if (current.IsValid && ImGui.IsItemHovered())
			{
				ImGui.BeginTooltip();
				ImGuiSafe.TextSafe($"Path: {current.PrefabPath}");
				ImGuiSafe.TextSafe($"GUID: {current.PrefabGuid}");
				ImGui.EndTooltip();
			}

			// Right-click: clear
			if (ImGui.BeginPopupContextItem($"prreflist_ctx_{index}"))
			{
				if (ImGui.Selectable("Clear"))
					_list[index] = default(PrefabReference);
				ImGui.EndPopup();
			}

			if (!_isArray)
			{
				ImGui.SameLine();
				if (ImGui.Button($"X##prrefrem_{index}", new Num.Vector2(removeButtonWidth, 0)))
					removeAt = index;
			}

			ImGui.PopID();
		}

		void DrawPrefabRefPickerPopup()
		{
			if (_pendingPickerIndex >= 0)
			{
				ImGui.OpenPopup($"reflist_prpicker_{_scopeId}");
				_activePickerIndex  = _pendingPickerIndex;
				_pendingPickerIndex = -1;
				_pickerSearch       = string.Empty;
			}

			if (_activePickerIndex < 0 || _activePickerIndex >= _list.Count)
				return;

			var center = new Num.Vector2(Screen.Width * 0.5f, Screen.Height * 0.5f);
			ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Num.Vector2(0.5f, 0.5f));
			ImGui.SetNextWindowSize(new Num.Vector2(420, 460), ImGuiCond.Appearing);

			bool open = true;
			if (!ImGui.BeginPopupModal($"reflist_prpicker_{_scopeId}", ref open, ImGuiWindowFlags.NoResize))
				return;

			var current = (PrefabReference)_list[_activePickerIndex];
			ImGuiSafe.TextColoredSafe(new Num.Vector4(0.8f, 0.5f, 1f, 1f), $"{_name}[{_activePickerIndex}]  (PrefabReference)");
			ImGui.Separator();

			ImGui.SetNextItemWidth(-1);
			ImGui.InputTextWithHint("##prreflistsearch", "Search...", ref _pickerSearch, 128);
			ImGui.Separator();

			if (ImGui.Selectable("  None (PrefabReference)", !current.IsValid))
			{
				_list[_activePickerIndex] = default(PrefabReference);
				_activePickerIndex = -1;
				ImGui.CloseCurrentPopup();
			}

			ImGui.Separator();

			var entries = PrefabReferenceTypeInspector.CollectPrefabEntriesInternal();
			string lower   = _pickerSearch.ToLowerInvariant();
			bool filtering = !string.IsNullOrEmpty(_pickerSearch);
			string lastFolder = null;

			for (int i = 0; i < entries.Count; i++)
			{
				var entry = entries[i];
				if (filtering && !entry.Name.ToLowerInvariant().Contains(lower))
					continue;

				if (!filtering && entry.SubFolder != lastFolder)
				{
					if (lastFolder != null) ImGui.Separator();
					ImGuiSafe.TextDisabledSafe(entry.SubFolder ?? "Prefabs");
					lastFolder = entry.SubFolder;
				}

				bool isCurrent = current.IsValid &&
					string.Equals(current.PrefabName, entry.Name, StringComparison.OrdinalIgnoreCase);

				ImGui.PushID(i);
				if (ImGui.Selectable($"  {entry.Name}", isCurrent))
				{
					_list[_activePickerIndex] = PrefabReferenceTypeInspector.BuildPrefabRef(entry.AbsolutePath, entry.Name);
					_activePickerIndex = -1;
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
			{
				_activePickerIndex = -1;
				ImGui.CloseCurrentPopup();
			}

			ImGui.EndPopup();
		}

		Entity ResolveEntityAt(int index)
		{
			if (_elementKind == ElementKind.Transform)
				return (_list[index] as Transform)?.Entity;
			return _list[index] as Entity;
		}

		void AssignEntityAt(int index, Entity entity)
		{
			_list[index] = _elementKind == ElementKind.Transform
				? (object) entity?.Transform
				: entity;
		}

		void ResizeArray(int newSize)
		{
			var newArray = Array.CreateInstance(_elementType, newSize);
			Array.Copy((Array) _list, newArray, Math.Min(_list.Count, newSize));
			SetValue(newArray);
			_list = newArray;
		}

		void RefreshList()
		{
			var current = _getter(_target) as IList;
			if (current != null)
			{
				_list = current;
				return;
			}

			// Field is null — create a default empty collection
			if (_isArray)
			{
				_list = Array.CreateInstance(_elementType, 0);
			}
			else
			{
				var listType = typeof(List<>).MakeGenericType(_elementType);
				_list = (IList) Activator.CreateInstance(listType);
			}
		}

		private void SpecialCasesHandling()
		{
			if (_target is PolygonCollider polyCollider)
				polyCollider.UpdateShapeFromPoints();
		}
	}
}
