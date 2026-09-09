using ImGuiNET;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Voltage.Data;
using Voltage.Editor.DebugUtils;
using Voltage.Editor.ImGuiCore;
using Voltage.Editor.Inspectors.ObjectInspectors;
using Voltage.Editor.ProjectFile;
using Voltage.Editor.Serialization;
using Voltage.Editor.Undo.ComponentActions;
using Voltage.Editor.Undo.Core;
using Voltage.Editor.Undo.PrefabActions;
using Voltage.Editor.Undo.PropertyActions;
using Voltage.Editor.Utils;
using Voltage.Persistence;
using Voltage.Serialization;
using Voltage.Utils;
using Voltage.Utils.Coroutines;
using Num = System.Numerics;

namespace Voltage.Editor.Windows;

public class EntityInspectorWindow
{
	private static int _nextId = 0;
	private readonly int _id;
	private readonly bool _isMain;

	public Entity Entity { get; private set; }
	public float Width { get; set; } = 500f; 
	public bool IsOpen { get; set; } = true; 

	private TransformInspector _transformInspector;
	private readonly List<IComponentInspector> _componentInspectors = new();

	private bool _isEditingUpdateInterval = false;
	private uint _updateIntervalEditStartValue;
	private bool _isEditingName = false;
	private string _nameEditStartValue;
	private readonly int _offsetDuringCreation;

	// SerializedPrefab creation / confirmation popup fields
	private string _prefabName = "";
	private bool _showApplyToPrefabCopiesConfirmation = false;
	private List<Entity> _prefabCopiesToModify = new();
	private bool _showApplyToOriginalPrefabConfirmation = false;

	private bool _showRevertAllOverridesConfirmation = false;
	private string _revertComponentName = null; // non-null triggers single-component revert popup
	private ImGuiManager _imGuiManager;
	private Entity _lockedEntity;

	// Add Component fields
	private bool _showAddComponentPopup = false;
	private string _componentFilterText = "";
	private static List<Type> _cachedComponentTypes; // Cache to avoid repeated reflection
	private bool _shouldFocusWindow;

	public EntityInspectorWindow(ImGuiManager manager, Entity entity = null, bool isMain = false, int offsetDuringCreation = 0)
	{
		_id = _nextId++;
		_isMain = isMain;
		_imGuiManager = manager;
		Entity = entity;
		_offsetDuringCreation = offsetDuringCreation;
		InitInspector();
	}

	private void InitInspector()
	{
		_componentInspectors.Clear();

		if (_imGuiManager.SceneGraphWindow.EntityPane.SelectedEntities.Count == 1 && Entity != null)
		{
			_transformInspector = new TransformInspector(Entity.Transform);
			for (var i = 0; i < Entity.Components.Count; i++)
				_componentInspectors.Add(ComponentInspectorFactory.CreateInspector(Entity.Components[i]));
		}
		else if (_imGuiManager.SceneGraphWindow.EntityPane.SelectedEntities.Count > 1)
		{
			var commonComponents = GetCommonComponents(_imGuiManager.SceneGraphWindow.EntityPane.SelectedEntities);
			_componentInspectors.Clear();
			foreach (var compType in commonComponents)
			{
				var multiInspector = new MultiComponentInspector(compType, _imGuiManager.SceneGraphWindow.EntityPane.SelectedEntities);
				_componentInspectors.Add(multiInspector);
			}
		}

		if (_cachedComponentTypes == null)
		{
			CacheAvailableComponentTypes();
		}
	}

	public void Draw()
	{
		if (!IsOpen)
			return;

		var open = IsOpen;

		if (_isMain)
		{
			Gui.Begin("Main Entity Inspector###MainEntityInspector");
		}
		else
		{
			Gui.Begin($"Entity Inspector###EntityInspector_{_id}", ref open);
		}

		if (_shouldFocusWindow)
		{
			ImGui.SetWindowFocus();
			_shouldFocusWindow = false;
		}

		// If more than one entity is selected
		if (_imGuiManager.SceneGraphWindow.EntityPane.SelectedEntities.Count > 1 && !_imGuiManager.IsInspectorTabLocked)
		{
			ImGui.SetWindowFontScale(1.5f);
			ImGui.Text("Multiple Entities Selected");
			ImGui.SetWindowFontScale(1.0f);

			ImGui.PushFont(ImGui.GetIO().FontDefault); // Use default font (smallest)
			ImGui.PushStyleColor(ImGuiCol.Text, new Num.Vector4(0.8f, 0.8f, 0.8f, 1.0f));
			for (int i = 0; i < _imGuiManager.SceneGraphWindow.EntityPane.SelectedEntities.Count; i++)
			{
				ImGuiSafe.TextSafe($"{i + 1}. {_imGuiManager.SceneGraphWindow.EntityPane.SelectedEntities[i].Name}");
			}

			ImGui.PopStyleColor();
			ImGui.PopFont();

			VoltageEditorUtils.BigVerticalSpace();

			// Every entity has these regardless of which components they share, so they are always editable.
			DrawMultiEntityBasicInfo(_imGuiManager.SceneGraphWindow.EntityPane.SelectedEntities);

			VoltageEditorUtils.BigVerticalSpace();

			// Show common Components
			foreach (var inspector in _componentInspectors)
			{
				inspector.Draw();
				VoltageEditorUtils.MediumVerticalSpace();
			}
		}
		else if (_imGuiManager.SceneGraphWindow.EntityPane.SelectedEntities.Count == 1 && Entity != null || _lockedEntity != null)
		{
			var entityName = Entity.Name;
			ImGui.SetWindowFontScale(1.5f);
			ImGuiSafe.TextSafe(entityName);
			ImGui.SetWindowFontScale(1.0f);

			float spacing = 12f * _imGuiManager.FontSizeMultiplier;
			float iconSize = 20f * _imGuiManager.FontSizeMultiplier;
			ImGui.SameLine(0, spacing);

			// Lock Mode Button
			Num.Vector4 lockedButtonColor;
			if (_imGuiManager.IsInspectorTabLocked)
			{
				lockedButtonColor = new Num.Vector4(0.2f, 0.5f, 1f, 1f); // blue
				ImGui.PushStyleColor(ImGuiCol.Button, lockedButtonColor);
				if(Gui.ImageButton("Lock Off", ImguiImageLoader.LockedInspectorIconId, new Num.Vector2(iconSize, iconSize)))
				{
					_imGuiManager.IsInspectorTabLocked = false;
					_lockedEntity = null;
				}

				if (ImGui.IsItemHovered())
					ImGui.SetTooltip("Unlock the current inspector");
			}
			else
			{
				lockedButtonColor = ImGui.GetStyle().Colors[(int)ImGuiCol.Button];
				ImGui.PushStyleColor(ImGuiCol.Button, lockedButtonColor);
				if (Gui.ImageButton("Lock On", ImguiImageLoader.UnlockedInspectorIconId, new Num.Vector2(iconSize, iconSize)))
				{
					_imGuiManager.IsInspectorTabLocked = true;
					_lockedEntity = Entity;
				}

				if (ImGui.IsItemHovered())
					ImGui.SetTooltip("Lock the current inspector");
			}

			ImGui.PopStyleColor();
			VoltageEditorUtils.BigVerticalSpace();

			if (Entity == null)
			{
				ImGui.TextColored(new Num.Vector4(1, 1, 0, 1), "No entity selected.");
			}
			else
			{
				var type = Entity.Type.ToString();
				Gui.InputText("InstanceType", ref type, 30);

				// Show OriginalPrefabName / GUID for SerializedPrefab entities (readonly).
				if (Entity.Type == Entity.InstanceType.SerializedPrefab && !string.IsNullOrEmpty(Entity.OriginalPrefabName))
				{
					var originalPrefabName = Entity.OriginalPrefabName;
					Gui.InputText("Original SerializedPrefab Name", ref originalPrefabName, 50,
						ImGuiInputTextFlags.ReadOnly);

					// Phase 3: show the stable GUID when present (absent on pre-Phase-3 entities).
					if (Entity.OriginalPrefabGuid != Guid.Empty)
					{
						var guidStr = Entity.OriginalPrefabGuid.ToString();
						Gui.InputText("Prefab GUID", ref guidStr, 40, ImGuiInputTextFlags.ReadOnly);
					}

					// Phase 4b: show component-level override state and revert controls.
					DrawPrefabOverridePanel();
				}

				#region Entity Basic Info
				// Enabled
				{
					bool oldEnabled = Entity.Enabled;
					bool enabled = oldEnabled;
					if (Gui.Checkbox("Enabled", ref enabled) && enabled != oldEnabled)
					{
						EditorChangeTracker.PushUndo(
							new GenericValueChangeAction(
								Entity,
								typeof(Entity).GetProperty(nameof(Entity.Enabled)),
								oldEnabled,
								enabled,
								$"{Entity.Name}.Enabled"
							),
							Entity,
							$"{Entity.Name}.Enabled"
						);
						Entity.SetEnabled(enabled);
					}
				}

				// Name (edit session)
				{
					string name = Entity.Name;
					bool changed = Gui.InputText("Name", ref name, 25);

					if (ImGui.IsItemActive() && !_isEditingName)
					{
						_isEditingName = true;
						_nameEditStartValue = Entity.Name;
					}

					if (_isEditingName && ImGui.IsItemDeactivatedAfterEdit())
					{
						_isEditingName = false;
						Entity.Name = name;

						if (Entity.Name != _nameEditStartValue)
						{
							EditorChangeTracker.PushUndo(
								new GenericValueChangeAction(
									Entity,
									typeof(Entity).GetProperty(nameof(Entity.Name)),
									_nameEditStartValue,
									Entity.Name,
									$"{_nameEditStartValue}.Name"
								),
								Entity,
								$"{_nameEditStartValue}.Name"
							);
						}
					}
				}


				// UpdateOrder
				{
					int oldUpdateOrder = Entity.UpdateOrder;
					int updateOrder = oldUpdateOrder;
					if (Gui.InputInt("Update Order", ref updateOrder) && updateOrder != oldUpdateOrder)
					{
						EditorChangeTracker.PushUndo(
							new GenericValueChangeAction(
								Entity,
								typeof(Entity).GetProperty(nameof(Entity.UpdateOrder)),
								oldUpdateOrder,
								updateOrder,
								$"{Entity.Name}.UpdateOrder"
							),
							Entity,
							$"{Entity.Name}.UpdateOrder"
						);
						Entity.SetUpdateOrder(updateOrder);
					}
				}

				// UpdateInterval
				{
					int updateInterval = (int)Entity.UpdateInterval;

					bool changed = Gui.SliderInt("Update Interval", ref updateInterval, 1, 100);

					// Start of edit session: store the initial value
					if (ImGui.IsItemActive() && !_isEditingUpdateInterval)
					{
						_isEditingUpdateInterval = true;
						_updateIntervalEditStartValue = Entity.UpdateInterval;
					}

					// Apply the value while dragging
					if (changed)
						Entity.UpdateInterval = (uint)updateInterval;

					// End of edit session: push undo if value changed
					if (_isEditingUpdateInterval && ImGui.IsItemDeactivatedAfterEdit())
					{
						_isEditingUpdateInterval = false;
						if (Entity.UpdateInterval != _updateIntervalEditStartValue)
						{
							EditorChangeTracker.PushUndo(
								new GenericValueChangeAction(
									Entity,
									(obj, val) => ((Entity)obj).UpdateInterval = (uint)val,
									_updateIntervalEditStartValue,
									Entity.UpdateInterval,
									$"{Entity.Name}.UpdateInterval"
								),
								Entity,
								$"{Entity.Name}.UpdateInterval"
							);
						}
					}
				}

				// Tag
				{
					var tags = Project.ProjectSettings.Instance.Entities.EntityTags;
					var tagNames = new List<string>();
					var tagValues = new List<int>();

					foreach (var kvp in tags)
					{
						tagNames.Add(kvp.Key);
						tagValues.Add(kvp.Value);
					}

					var tagNamesArray = tagNames.ToArray();
					var tagValuesArray = tagValues.ToArray();

					int oldTag = Entity.Tag;
					var selectedIndex = System.Array.IndexOf(tagValuesArray, oldTag);
					if (selectedIndex < 0)
						selectedIndex = 0;

					if (Gui.Combo("Tag", ref selectedIndex, tagNamesArray, tagNamesArray.Length))
					{
						var newTag = tagValuesArray[selectedIndex];
						if (newTag != oldTag)
						{
							EditorChangeTracker.PushUndo(
								new GenericValueChangeAction(
									Entity,
									typeof(Entity).GetProperty(nameof(Entity.Tag)),
									oldTag,
									newTag,
									$"{Entity.Name}.Tag"
								),
								Entity,
								$"{Entity.Name}.Tag"
							);
							Entity.Tag = newTag;
						}
					}
				}

				VoltageEditorUtils.MediumVerticalSpace();
				
				// CanBeSelected
				{
					bool oldSelectable = Entity.CanBeSelected;
					bool isSelectable = oldSelectable;
					if (Gui.Checkbox("Can Be Selected", ref isSelectable) && isSelectable != oldSelectable)
					{
						EditorChangeTracker.PushUndo(
							new GenericValueChangeAction(
								Entity,
								typeof(Entity).GetProperty(nameof(Entity.CanBeSelected)),
								oldSelectable,
								isSelectable,
								$"{Entity.Name}.CanBeSelected"
							),
							Entity,
							$"{Entity.Name}.CanBeSelected"
						);
						Entity.CanBeSelected = isSelectable;
					}

					if (ImGui.IsItemHovered())
					{
						ImGui.SetTooltip("If FALSE, you won't be able select this \n Entity with your cursor in the game window.");
					}
				}

				// DebugRenderEnabled
				{
					bool oldDebugEnabled = Entity.DebugRenderEnabled;
					bool debugEnabled = oldDebugEnabled;
					if (Gui.Checkbox("Debug Render Enabled", ref debugEnabled) && debugEnabled != oldDebugEnabled)
					{
						EditorChangeTracker.PushUndo(
							new GenericValueChangeAction(
								Entity,
								typeof(Entity).GetProperty(nameof(Entity.DebugRenderEnabled)),
								oldDebugEnabled,
								debugEnabled,
								$"{Entity.Name}.DebugRenderEnabled"
							),
							Entity,
							$"{Entity.Name}.DebugRenderEnabled"
						);
						Entity.DebugRenderEnabled = debugEnabled;
					}
				}
				#endregion

				VoltageEditorUtils.MediumVerticalSpace();

				if (_transformInspector != null)
				{
					_transformInspector.Draw();
				}

				VoltageEditorUtils.MediumVerticalSpace();

				for (var i = _componentInspectors.Count - 1; i >= 0; i--)
				{
					if (_componentInspectors[i].Entity == null)
					{
						_componentInspectors.RemoveAt(i);
						continue;
					}

					_componentInspectors[i].Draw();
					VoltageEditorUtils.MediumVerticalSpace();
				}

				DrawMissingComponentCards();

				// Add Component button
				if (VoltageEditorUtils.CenteredButton("Add Component", 0.6f))
				{
					_showAddComponentPopup = true;
					_componentFilterText = "";
				}

				VoltageEditorUtils.MediumVerticalSpace();

				if (Entity.Type != Entity.InstanceType.NonSerialized && VoltageEditorUtils.CenteredButton("Create SerializedPrefab", 0.6f))
				{
					_prefabName = Entity.Name + "_Prefab";
					ImGui.OpenPopup("prefab-creator");
				}

				if (Entity.Type == Entity.InstanceType.SerializedPrefab && !string.IsNullOrEmpty(Entity.OriginalPrefabName))
				{
					VoltageEditorUtils.MediumVerticalSpace();
					if (VoltageEditorUtils.CenteredButton("Apply to SerializedPrefab Copies", 0.8f))
					{
						ShowApplyToPrefabCopiesConfirmation();
					}
				}

				if (Entity.Type == Entity.InstanceType.SerializedPrefab && !string.IsNullOrEmpty(Entity.OriginalPrefabName))
				{
					VoltageEditorUtils.MediumVerticalSpace();
					if (VoltageEditorUtils.CenteredButton("Apply to Original SerializedPrefab", 0.8f))
					{
						_showApplyToOriginalPrefabConfirmation = true;
					}
				}

				DrawAddComponentPopup();
				DrawPrefabCreatorPopup();
				DrawApplyToPrefabCopiesConfirmationPopup();
				DrawApplyToOriginalPrefabConfirmationPopup();
				DrawRevertAllOverridesConfirmationPopup();
				DrawRevertComponentOverrideConfirmationPopup();
			}
		}
		else
		{
			ImGui.TextColored(new Num.Vector4(1, 1, 0, 1), "No entity selected.");
		}

		IsOpen = open;

		Gui.End();
	}


	/// <summary>
	/// Invalidates the cached component types so they are rebuilt on next access.
	/// Call this after script recompilation to pick up new/changed component types.
	/// </summary>
	public static void InvalidateComponentTypeCache()
	{
		_cachedComponentTypes = null;
	}

	private static void CacheAvailableComponentTypes()
	{
		var assemblies = AppDomain.CurrentDomain.GetAssemblies();

		// Use a dictionary keyed by FullName so that types from newer (recompiled)
		// assemblies overwrite duplicates from older ones that are still in memory.
		var typesByFullName = new Dictionary<string, Type>();

		foreach (var assembly in assemblies)
		{
			// Skip stale dynamically compiled script assemblies.
			var assemblyName = assembly.GetName().Name;
			if (assemblyName != null && assemblyName.StartsWith("DynamicScripts")
				&& assembly != Core.LatestScriptAssembly)
				continue;

			try
			{
				var componentTypes = assembly.GetTypes()
					.Where(t => typeof(Component).IsAssignableFrom(t)
					            && !t.IsAbstract
					            && !t.IsInterface
					            && HasParameterlessConstructor(t));

				foreach (var type in componentTypes)
				{
					// Later assemblies (e.g. recompiled scripts) overwrite earlier ones
					typesByFullName[type.FullName] = type;
				}
			}
			catch (ReflectionTypeLoadException ex)
			{
				Debug.Warn($"Failed to load types from assembly {assembly.FullName}: {ex.Message}");
				continue;
			}
		}

		_cachedComponentTypes = typesByFullName.Values.OrderBy(t => t.Name).ToList();

		Debug.Log($"Cached {_cachedComponentTypes.Count} component types from {assemblies.Length} assemblies");
	}

	/// <summary>
	/// Checks if a type has a parameterless constructor (public or non-public).
	/// </summary>
	private static bool HasParameterlessConstructor(Type type)
	{
		var publicConstructor = type.GetConstructor(
			BindingFlags.Public | BindingFlags.Instance,
			null,
			Type.EmptyTypes,
			null
		);

		if (publicConstructor != null)
			return true;

		var nonPublicConstructor = type.GetConstructor(
			BindingFlags.NonPublic | BindingFlags.Instance,
			null,
			Type.EmptyTypes,
			null
		);

		return nonPublicConstructor != null;
	}

	/// <summary>
	/// Filters available component types based on search text.
	/// </summary>
	private List<Type> GetFilteredComponentTypes()
	{
		if (_cachedComponentTypes == null)
			CacheAvailableComponentTypes();

		if (string.IsNullOrWhiteSpace(_componentFilterText))
			return _cachedComponentTypes;

		return _cachedComponentTypes
			.Where(t => t.Name.Contains(_componentFilterText, StringComparison.OrdinalIgnoreCase) ||
						t.FullName.Contains(_componentFilterText, StringComparison.OrdinalIgnoreCase))
			.ToList();
	}

	/// <summary>
	/// Adds a component of the specified type to the entity.
	/// </summary>
	private void AddComponentToEntity(Type componentType)
	{
		if (Entity == null || componentType == null)
			return;

		try
		{
			// Resolve and satisfy [RequireComponent] dependencies on this entity first
			var requirements = Component.ResolveRequiredComponents(componentType);
			foreach (var requiredType in requirements)
			{
				if (Entity.GetComponent(requiredType) != null)
					continue;

				var required = (Component)Activator.CreateInstance(requiredType);
				required.SetSerialized(true);
				Entity.AddComponent(required, true);

				EditorChangeTracker.PushUndo(
					new ComponentAddedUndoAction(Entity, required),
					Entity,
					$"Auto-add required {requiredType.Name} to {Entity.Name}"
				);
			}

			// Add the requested component itself
			var component = (Component)Activator.CreateInstance(componentType);
			component.SetSerialized(true);
			Entity.AddComponent(component, true);
			DelayedSetEntity(Entity);

			EditorChangeTracker.PushUndo(
				new ComponentAddedUndoAction(Entity, component),
				Entity,
				$"Add {componentType.Name} to {Entity.Name}"
			);

			// Resolve and satisfy [RequireComponentInChildren] and create child entities as needed
			var childRequirements = Component.ResolveRequiredComponentsInChildren(componentType);
			foreach (var attr in childRequirements)
			{
				// Check if any existing child already has this component
				bool satisfiedByExistingChild = false;
				for (var i = 0; i < Entity.Transform.ChildCount; i++)
				{
					if (Entity.Transform.GetChild(i).Entity.GetComponent(attr.ComponentType) != null)
					{
						satisfiedByExistingChild = true;
						break;
					}
				}

				if (satisfiedByExistingChild)
					continue;

				var childName = !string.IsNullOrWhiteSpace(attr.ChildEntityName)
					? attr.ChildEntityName
					: attr.ComponentType.Name;

				var childEntity = Core.Scene.AddEntity(new Entity(childName, Entity.InstanceType.Serialized));
				childEntity.Parent = Entity.Transform;

				var childComponent = (Component)Activator.CreateInstance(attr.ComponentType);
				childComponent.SetSerialized(true);
				childEntity.AddComponent(childComponent, true);

				EditorChangeTracker.PushUndo(
					new ComponentAddedUndoAction(childEntity, childComponent),
					childEntity,
					$"Auto-create child '{childName}' with {attr.ComponentType.Name} required by '{componentType.Name}'"
				);
			}
		}
		catch (Exception ex)
		{
			Debug.Error($"Failed to add component {componentType.Name}: {ex.Message}");
		}
	}


	/// <summary>
	/// Cards for serialized component entries whose type cannot be resolved in this editor session
	/// (plugin not installed, script missing). Their raw data is preserved across saves; the Remove
	/// button here is the ONLY code path that ever drops such an entry.
	/// </summary>
	private void DrawMissingComponentCards()
	{
		var entityData = Entity?.GetEntityData();
		if (entityData?.ComponentDataList == null)
			return;

		List<ComponentDataEntry> missing = null;
		foreach (var entry in entityData.ComponentDataList)
		{
			if (Scene.IsUnresolvableComponentEntry(entry))
				(missing ??= new List<ComponentDataEntry>()).Add(entry);
		}

		if (missing == null)
			return;

		var warnColor = new System.Numerics.Vector4(1f, 0.8f, 0.2f, 1f);

		foreach (var entry in missing)
		{
			var shortName = entry.ComponentTypeName?.Split('.').LastOrDefault() ?? entry.ComponentId ?? "(unknown)";
			ImGui.PushID($"missing_{entry.ComponentId}_{entry.ComponentName}");

			ImGui.PushStyleColor(ImGuiCol.Border, warnColor);
			ImGui.BeginChild("missingcard",
				new System.Numerics.Vector2(0, ImGui.GetFrameHeightWithSpacing() * 2.2f), true);

			ImGui.TextColored(warnColor, $"Missing Component: {shortName}");
			if (ImGui.IsItemHovered())
				ImGui.SetTooltip($"Type: {entry.ComponentTypeName ?? "-"}\nComponentId: {entry.ComponentId ?? "-"}\n\n" +
					"This component's type is not loaded — it likely comes from a plugin that is not installed " +
					"or a script that no longer compiles. Its data is preserved when saving.");

			ImGui.TextDisabled($"id: {entry.ComponentId ?? "-"}");
			ImGui.SameLine();
			if (Gui.SmallButton("Remove"))
				ImGui.OpenPopup("confirm-remove-missing");

			if (Gui.BeginPopup("confirm-remove-missing"))
			{
				ImGui.TextWrapped($"Permanently remove the preserved data of '{shortName}'? If the missing plugin is installed later, this component will be gone.");
				if (Gui.Button("Remove permanently"))
				{
					entityData.ComponentDataList.Remove(entry);
					ImGui.CloseCurrentPopup();
				}
				ImGui.SameLine();
				if (Gui.Button("Cancel"))
					ImGui.CloseCurrentPopup();
				Gui.EndPopup();
			}

			ImGui.EndChild();
			ImGui.PopStyleColor();
			ImGui.PopID();

			VoltageEditorUtils.MediumVerticalSpace();
		}
	}

	/// <summary>
	/// Draws the Add Component popup with search and selection.
	/// </summary>
	private void DrawAddComponentPopup()
	{
		if (_showAddComponentPopup)
		{
			ImGui.OpenPopup("add-component-popup");
			_showAddComponentPopup = false;
		}

		var center = new Num.Vector2(Screen.Width * 0.5f, Screen.Height * 0.4f);
		ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Num.Vector2(0.5f, 0.5f));
		ImGui.SetNextWindowSize(new Num.Vector2(400, 500), ImGuiCond.Appearing);

		bool open = true;
		if (Gui.BeginPopupModal("add-component-popup", ref open, ImGuiWindowFlags.NoResize))
		{
			ImGui.Text("Add Component");
			ImGui.Separator();

			// Search/filter box
			ImGui.Text("Search:");
			Gui.InputText("##ComponentFilter", ref _componentFilterText, 50);

			VoltageEditorUtils.SmallVerticalSpace();

			var componentTypes = GetFilteredComponentTypes();

			ImGuiSafe.TextSafe($"Available Components ({componentTypes.Count}):");
			ImGui.Separator();

			if (ImGui.BeginChild("ComponentList", new Num.Vector2(0, 350), true))
			{
				foreach (var componentType in componentTypes)
				{
					// Show namespace and type name for clarity
					var displayName = $"{componentType.Name}";
					var namespace_text = $"({componentType.Namespace})";

					if (Gui.Selectable(displayName))
					{
						AddComponentToEntity(componentType);
						ImGui.CloseCurrentPopup();
					}

					if (ImGui.IsItemHovered())
					{
						ImGuiSafe.SetTooltipSafe($"{componentType.FullName}");
					}

					// Show namespace in smaller text
					ImGui.SameLine();
					ImGui.PushStyleColor(ImGuiCol.Text, new Num.Vector4(0.6f, 0.6f, 0.6f, 1.0f));
					ImGuiSafe.TextSafe(namespace_text);
					ImGui.PopStyleColor();
				}
			}
			ImGui.EndChild();

			VoltageEditorUtils.SmallVerticalSpace();

			// Close button
			var buttonWidth = 80f;
			var windowWidth = ImGui.GetWindowSize().X;
			var centerStart = (windowWidth - buttonWidth) * 0.5f;

			ImGui.SetCursorPosX(centerStart);

			if (Gui.Button("Cancel", new Num.Vector2(buttonWidth, 0)))
			{
				_componentFilterText = "";
				ImGui.CloseCurrentPopup();
			}

			Gui.EndPopup();
		}
	}

	#region Multi-selection basic info

	private bool _isEditingMultiUpdateInterval;

	// Per-entity, not one shared value: a mixed selection must undo back to each entity's own interval.
	private readonly List<(Entity Entity, uint Value)> _multiUpdateIntervalEditStart = new();

	/// <summary>
	/// The basic Entity fields for a multi-selection. Every entity has these whether or not their components line
	/// up, so they stay editable: the first selected entity supplies the displayed value, an edit is applied to all
	/// of them, and the whole batch reverts as one undo step. Fields whose values disagree are marked "mixed".
	/// Name is deliberately left out - it identifies an entity, so setting them all to one string is never useful.
	/// </summary>
	private void DrawMultiEntityBasicInfo(List<Entity> entities)
	{
		if (entities == null || entities.Count == 0)
			return;

		if (!Gui.CollapsingHeader($"Entity ({entities.Count} selected)", ImGuiTreeNodeFlags.DefaultOpen))
			return;

		MultiCheckbox(entities, "Enabled", null,
			e => e.Enabled, (e, v) => e.SetEnabled(v));

		MultiInputInt(entities, "Update Order",
			e => e.UpdateOrder, (e, v) => e.SetUpdateOrder(v));

		MultiUpdateInterval(entities);

		MultiTag(entities);

		VoltageEditorUtils.MediumVerticalSpace();

		MultiCheckbox(entities, "Can Be Selected",
			"If FALSE, you won't be able select these \n Entities with your cursor in the game window.",
			e => e.CanBeSelected, (e, v) => e.CanBeSelected = v);

		MultiCheckbox(entities, "Debug Render Enabled", null,
			e => e.DebugRenderEnabled, (e, v) => e.DebugRenderEnabled = v);
	}

	/// <summary>The shared value across the selection, or <c>default</c> plus <paramref name="mixed"/> when they disagree.</summary>
	private static T SharedValue<T>(List<Entity> entities, Func<Entity, T> get, out bool mixed)
	{
		mixed = false;
		var value = default(T);
		var first = true;

		foreach (var entity in entities)
		{
			if (entity == null)
				continue;

			var current = get(entity);

			if (first)
			{
				value = current;
				first = false;
			}
			else if (!Equals(value, current))
			{
				mixed = true;
			}
		}

		return value;
	}

	/// <summary>Writes one value to every selected entity and pushes the whole batch as a single undo step.</summary>
	private static void ApplyToAll<T>(List<Entity> entities, Func<Entity, T> get, Action<Entity, T> set,
		T value, string fieldName)
	{
		var actions = new List<EditorChangeTracker.IEditorAction>();

		foreach (var entity in entities)
		{
			if (entity == null)
				continue;

			var old = get(entity);
			if (Equals(old, value))
				continue;

			set(entity, value);

			actions.Add(new GenericValueChangeAction(entity,
				(obj, val) => set((Entity)obj, (T)val), old, value, $"{entity.Name}.{fieldName}"));

			EditorChangeTracker.MarkChanged(entity, $"{entity.Name}.{fieldName}");
		}

		if (actions.Count == 0)
			return;

		EditorChangeTracker.PushUndo(new CompositeUndoAction(actions, $"{actions.Count} entities: {fieldName}"));
	}

	/// <summary>A mixed selection shows unticked, so the click that resolves it turns the field on everywhere.</summary>
	private static void MultiCheckbox(List<Entity> entities, string label, string tooltip,
		Func<Entity, bool> get, Action<Entity, bool> set)
	{
		var shared = SharedValue(entities, get, out var mixed);
		var value = !mixed && shared;

		if (Gui.Checkbox(label, ref value) && (mixed || value != shared))
			ApplyToAll(entities, get, set, value, label);

		if (tooltip != null && ImGui.IsItemHovered())
			ImGui.SetTooltip(tooltip);

		MixedHint(mixed);
	}

	private static void MultiInputInt(List<Entity> entities, string label,
		Func<Entity, int> get, Action<Entity, int> set)
	{
		var shared = SharedValue(entities, get, out var mixed);
		var value = shared;

		if (Gui.InputInt(label, ref value) && (mixed || value != shared))
			ApplyToAll(entities, get, set, value, label);

		MixedHint(mixed);
	}

	/// <summary>Dragged, so the undo step is pushed once when the slider is released rather than per frame.</summary>
	private void MultiUpdateInterval(List<Entity> entities)
	{
		var shared = SharedValue(entities, e => e.UpdateInterval, out var mixed);
		var value = (int)shared;

		var changed = Gui.SliderInt("Update Interval", ref value, 1, 100);

		if (ImGui.IsItemActive() && !_isEditingMultiUpdateInterval)
		{
			_isEditingMultiUpdateInterval = true;

			_multiUpdateIntervalEditStart.Clear();
			foreach (var entity in entities)
			{
				if (entity != null)
					_multiUpdateIntervalEditStart.Add((entity, entity.UpdateInterval));
			}
		}

		// Applied live so the drag reads back, but only committed to the undo stack once.
		if (changed)
		{
			foreach (var entity in entities)
			{
				if (entity != null)
					entity.UpdateInterval = (uint)value;
			}
		}

		if (_isEditingMultiUpdateInterval && ImGui.IsItemDeactivatedAfterEdit())
		{
			_isEditingMultiUpdateInterval = false;

			// The live drag already overwrote everything, so put each entity back on its own starting value
			// before ApplyToAll reads them - otherwise undo would flatten the selection instead of restoring it.
			foreach (var (entity, start) in _multiUpdateIntervalEditStart)
				entity.UpdateInterval = start;

			ApplyToAll(entities, e => e.UpdateInterval, (e, v) => e.UpdateInterval = v,
				(uint)value, "Update Interval");

			_multiUpdateIntervalEditStart.Clear();
		}

		MixedHint(mixed && !_isEditingMultiUpdateInterval);
	}

	private static void MultiTag(List<Entity> entities)
	{
		var tags = Project.ProjectSettings.Instance.Entities.EntityTags;
		var tagNames = new List<string>();
		var tagValues = new List<int>();

		foreach (var kvp in tags)
		{
			tagNames.Add(kvp.Key);
			tagValues.Add(kvp.Value);
		}

		var tagNamesArray = tagNames.ToArray();
		var tagValuesArray = tagValues.ToArray();

		var shared = SharedValue(entities, e => e.Tag, out var mixed);

		var selectedIndex = System.Array.IndexOf(tagValuesArray, shared);
		if (selectedIndex < 0)
			selectedIndex = 0;

		if (Gui.Combo("Tag", ref selectedIndex, tagNamesArray, tagNamesArray.Length))
		{
			var newTag = tagValuesArray[selectedIndex];
			if (mixed || newTag != shared)
				ApplyToAll(entities, e => e.Tag, (e, v) => e.Tag = v, newTag, "Tag");
		}

		MixedHint(mixed);
	}

	private static void MixedHint(bool mixed)
	{
		if (!mixed)
			return;

		ImGui.SameLine();
		ImGui.TextDisabled("(mixed)");
	}

	#endregion

	/// <summary>
	/// Finds the set of component types that are common to all selected entities.
	/// </summary>
	private List<Type> GetCommonComponents(List<Entity> entities)
	{
		if (entities == null || entities.Count == 0)
			return new List<Type>();

		var firstEntity = entities[0];
		var commonTypes = firstEntity.Components.Select(c => c.GetType().FullName).ToHashSet();

		foreach (var entity in entities.Skip(1))
		{
			var types = entity.Components.Select(c => c.GetType().FullName).ToHashSet();
			commonTypes.IntersectWith(types);
		}

		return firstEntity.Components
			.Where(c => commonTypes.Contains(c.GetType().FullName))
			.Select(c => c.GetType())
			.Distinct()
			.ToList();
	}

	/// <summary>
	/// Refreshes all component inspectors. Call this after components are added, removed, or replaced.
	/// </summary>
	public void RefreshComponentInspectors()
	{
		if (Entity == null)
			return;

		_componentInspectors.Clear();

		for (var i = 0; i < Entity.Components.Count; i++)
		{
			_componentInspectors.Add(ComponentInspectorFactory.CreateInspector(Entity.Components[i]));
		}
	}

	public void SetEntity(Entity entity)
	{
		if (_imGuiManager == null)
		{
			_imGuiManager = Core.GetGlobalManager<ImGuiManager>();
			return;
		}

		if (_imGuiManager.IsInspectorTabLocked)
			return;

		Entity = entity;
		_componentInspectors.Clear();
		_transformInspector = null;

		if (_imGuiManager.SceneGraphWindow.EntityPane.SelectedEntities.Count == 1 && Entity != null)
		{
			_transformInspector = new TransformInspector(Entity.Transform);
			RefreshComponentInspectors();
		}
		else if (_imGuiManager.SceneGraphWindow.EntityPane.SelectedEntities.Count > 1)
		{
			var commonComponents = GetCommonComponents(_imGuiManager.SceneGraphWindow.EntityPane.SelectedEntities);
			foreach (var compType in commonComponents)
			{
				var multiInspector = new MultiComponentInspector(compType, _imGuiManager.SceneGraphWindow.EntityPane.SelectedEntities);
				_componentInspectors.Add(multiInspector);
			}
		}
	}

	/// <summary>
	/// Draws the prefab creation popup with name input and create/cancel buttons.
	/// </summary>
	private void DrawPrefabCreatorPopup()
	{
		// Center the popup when it first appears
		var center = new Num.Vector2(Screen.Width * 0.5f, Screen.Height * 0.4f);
		ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Num.Vector2(0.5f, 0.5f));

		bool open = true;
		if (Gui.BeginPopupModal("prefab-creator", ref open, ImGuiWindowFlags.AlwaysAutoResize))
		{
			ImGui.Text("Create SerializedPrefab from Entity");
			ImGui.Separator();
			
			ImGui.Text("SerializedPrefab Name:");
			Gui.InputText("##PrefabName", ref _prefabName, 50);

			// Check if prefab name already exists and show warning
			var correctedName = CorrectPrefabName(_prefabName.Trim(), Entity.GetType().Name);
			bool prefabExists = CheckPrefabExists(correctedName);
			
			if (prefabExists)
			{
				ImGuiSafe.TextColoredSafe(new Num.Vector4(1.0f, 0.2f, 0.2f, 1.0f), $"Warning: SerializedPrefab '{correctedName}' already exists!");
			}

			VoltageEditorUtils.MediumVerticalSpace();

			// Center the buttons
			var buttonWidth = 80f;
			var spacing = 10f;
			var totalButtonWidth = buttonWidth * 2 + spacing;
			var windowWidth = ImGui.GetWindowSize().X;
			var centerStart = (windowWidth - totalButtonWidth) * 0.5f;
			
			ImGui.SetCursorPosX(centerStart);
			
			// Disable create button if prefab exists
			if (prefabExists)
				ImGui.BeginDisabled();
			
			if (Gui.Button("Create", new Num.Vector2(buttonWidth, 0)))
			{
				if (!string.IsNullOrWhiteSpace(_prefabName))
				{
					CreatePrefabFromEntity(correctedName);
					ImGui.CloseCurrentPopup();
				}
			}
			
			if (prefabExists)
				ImGui.EndDisabled();
			
			ImGui.SameLine();
			
			if (Gui.Button("Cancel", new Num.Vector2(buttonWidth, 0)))
			{
				ImGui.CloseCurrentPopup();
			}

			Gui.EndPopup();
		}
	}

	/// <summary>
	/// Checks if a prefab with the given name already exists.
	/// </summary>
	/// <param name="prefabName">Name of the prefab to check</param>
	/// <returns>True if prefab exists, false otherwise</returns>
	private bool CheckPrefabExists(string prefabName)
	{
		var prefabFilePath = $"{Path.Combine(ProjectManager.Instance.CurrentProject.PrefabsFolder, prefabName)}.prefab";
		return File.Exists(prefabFilePath);
	}

	/// <summary>
	/// Creates a prefab from the current entity using the DuplicateEntity method from EntityPane.
	/// Handles async saving and notifications.
	/// </summary>
	private async void CreatePrefabFromEntity(string prefabName, bool canOverride = false)
	{
		if (Entity == null || _imGuiManager?.SceneGraphWindow?.EntityPane == null)
			return;

		var newPrefab = _imGuiManager.SceneGraphWindow.EntityPane.DuplicateEntity(Entity, prefabName);

		if (newPrefab != null)
		{
			newPrefab.Type = Entity.InstanceType.SerializedPrefab;
			newPrefab.Name = prefabName;
			newPrefab.OriginalPrefabName = prefabName;

			bool saveSuccessful = await SerializationManager.Instance.SavePrefabDataAsync(newPrefab, canOverride);

			if (saveSuccessful)
			{
				_imGuiManager.SceneGraphWindow.AddPrefabToCache(newPrefab.Name);
				EditorDebug.Info($"Successfully created and saved prefab: {newPrefab.Name}");
			}
			else if(!canOverride)
			{
				EditorDebug.Error($"Failed to save prefab: {newPrefab.Name} - SerializedPrefab with this name already exists!");
			}
		}
		else
		{
			EditorDebug.Error($"Failed to create prefab: {prefabName}");
		}
	}

	/// <summary>
	/// Ensures the prefab name is unique within its EntityType directory.
	/// Only adds EntityType prefix if the name would conflict with existing prefabs.
	/// </summary>
	/// <param name="inputName">The user-provided prefab name</param>
	/// <param name="entityTypeName">The entity's type name</param>
	/// <returns>Unique prefab name</returns>
	private string CorrectPrefabName(string inputName, string entityTypeName)
	{
		if (string.IsNullOrWhiteSpace(inputName))
			return $"{entityTypeName}_Prefab";

		var cleanedName = inputName.Trim();
		
		// Check if prefab with this name already exists in the EntityType directory
		var prefabsDirectory = ProjectManager.Instance.CurrentProject.PrefabsFolder;

		if (!Directory.Exists(prefabsDirectory))
		{
			return cleanedName;
		}

		var prefabFilePath = Path.Combine(prefabsDirectory, $"{cleanedName}.prefab");
		
		if (!File.Exists(prefabFilePath))
		{
			return cleanedName;
		}

		// Name conflict detected - try to find a unique variant
		int suffix = 1;
		string uniqueName;
		
		do
		{
			uniqueName = $"{cleanedName}_{suffix}";
			prefabFilePath = Path.Combine(prefabsDirectory, $"{uniqueName}.prefab");
			suffix++;
		}
		while (File.Exists(prefabFilePath));

		return uniqueName;
	}

	/// <summary>
	/// Applies the current prefab entity's component data to all other entities in the scene 
	/// that have the same OriginalPrefabName.
	/// <para/>
	/// Public entry point used by the Scene Graph's "Apply to SerializedPrefab Copies" button while a
	/// prefab is being edited in isolation. Finds copies of the same prefab in the active scene and
	/// applies <paramref name="sourceEntity"/>'s component data to them. No-op when no copies exist.
	/// </summary>
	public void ApplyEntityToPrefabCopies(Entity sourceEntity)
	{
		if (sourceEntity == null || sourceEntity.Type != Entity.InstanceType.SerializedPrefab ||
			string.IsNullOrEmpty(sourceEntity.OriginalPrefabName))
			return;

		_prefabCopiesToModify = Core.Scene.Entities
			.Where(e => e != sourceEntity &&
						e.Type == Entity.InstanceType.SerializedPrefab &&
						e.OriginalPrefabName == sourceEntity.OriginalPrefabName)
			.ToList();

		if (_prefabCopiesToModify.Count == 0)
		{
			EditorDebug.Log($"No other copies of prefab '{sourceEntity.OriginalPrefabName}' found in scene.");
			return;
		}

		ApplyToPrefabCopies(sourceEntity);
	}

	private void ApplyToPrefabCopies(Entity sourceEntity)
	{
		if (sourceEntity == null || sourceEntity.Type != Entity.InstanceType.SerializedPrefab || string.IsNullOrEmpty(sourceEntity.OriginalPrefabName))
			return;

		// Use the pre-found list of prefab copies
		var prefabCopies = _prefabCopiesToModify;
		
		if (prefabCopies.Count == 0)
		{
			EditorDebug.Log($"No other copies of prefab '{sourceEntity.OriginalPrefabName}' found in scene.");
			return;
		}

		// Create undo action to track all changes
		var undoActions = new List<EditorChangeTracker.IEditorAction>();

		foreach (var targetEntity in prefabCopies)
		{
			// Store the entity's old component data for undo
			var oldComponentData = new Dictionary<string, ComponentData>();
			foreach (var component in targetEntity.Components)
			{
				if (component.Data != null)
				{
					try
					{
						var jsonSettings = new JsonSettings
						{
							PrettyPrint = false,
							TypeNameHandling = TypeNameHandling.Auto,
							PreserveReferencesHandling = false
						};
						
						// Deep clone the old data for undo
						var json = Json.ToJson(component.Data, jsonSettings);
						var clonedOldData = (ComponentData)Json.FromJson(json, component.Data.GetType());
						oldComponentData[component.Name] = clonedOldData;
					}
					catch (Exception ex)
					{
						Debug.Error($"Failed to backup component data for undo: {component.Name} - {ex.Message}");
					}
				}
			}

			// Apply component data from source entity to target entity
			bool hasChanges = false;
			foreach (var sourceComponent in sourceEntity.Components)
			{
				if (sourceComponent.Data == null)
					continue;

				// Find matching component in target entity
				var targetComponent = targetEntity.Components.FirstOrDefault(c => 
					c.GetType() == sourceComponent.GetType() && c.Name == sourceComponent.Name);

				if (targetComponent != null)
				{
					try
					{
						var jsonSettings = new JsonSettings
						{
							PrettyPrint = false,
							TypeNameHandling = TypeNameHandling.Auto,
							PreserveReferencesHandling = false
						};
						
						// Deep clone the source component data
						var json = Json.ToJson(sourceComponent.Data, jsonSettings);
						var clonedData = (ComponentData)Json.FromJson(json, sourceComponent.Data.GetType());
						
						// Apply the cloned data to the target component
						targetComponent.Data = clonedData;
						hasChanges = true;
					}
					catch (Exception ex)
					{
						Debug.Error($"Failed to copy component data: {sourceComponent.Name} - {ex.Message}");
					}
				}
			}

			// Only create undo action if there were actual changes
			if (hasChanges)
			{
				undoActions.Add(new PrefabCopyUndoAction(targetEntity, oldComponentData, sourceEntity.OriginalPrefabName));
			}
		}

		// Create a composite undo action that can undo all changes at once
		if (undoActions.Count > 0)
		{
			var compositeUndo = new CompositePrefabApplyUndoAction(undoActions, sourceEntity.OriginalPrefabName);
			EditorChangeTracker.PushUndo(
				compositeUndo,
				sourceEntity,
				$"Apply '{sourceEntity.OriginalPrefabName}' to {undoActions.Count} copies"
			);

			EditorDebug.Log($"Applied prefab '{sourceEntity.OriginalPrefabName}' to {undoActions.Count} copies.");
		}
		else
		{
			EditorDebug.Log($"No changes were applied - all copies are already up to date.");
		}

		_prefabCopiesToModify.Clear();
	}

	/// <summary>
	/// Used when we're dealing with a newly loaded entity that might not be ready to be set immediately.
	/// </summary>
	/// <param name="entity"></param>
	/// <param name="time"></param>
	public void DelayedSetEntity(Entity entity, float time = 0.05f)
	{
		Core.StartCoroutine(ShowInspector(entity, time));
	}

	private IEnumerator ShowInspector(Entity entity, float time)
	{
		yield return Coroutine.WaitForSeconds(time);
		SetEntity(entity);
	}

	/// <summary>
	/// Shows the confirmation popup for applying prefab changes to copies.
	/// </summary>
	private void ShowApplyToPrefabCopiesConfirmation()
	{
		if (Entity == null || Entity.Type != Entity.InstanceType.SerializedPrefab || string.IsNullOrEmpty(Entity.OriginalPrefabName))
			return;

		_prefabCopiesToModify = Core.Scene.Entities
			.Where(e => e != Entity &&
						e.Type == Entity.InstanceType.SerializedPrefab && 
						e.OriginalPrefabName == Entity.OriginalPrefabName)
			.ToList();

		if (_prefabCopiesToModify.Count == 0)
		{
			EditorDebug.Log($"No other copies of prefab '{Entity.OriginalPrefabName}' found in scene.");
			return;
		}

		_showApplyToPrefabCopiesConfirmation = true;
	}

	/// <summary>
	/// Draws the apply to prefab copies confirmation popup.
	/// </summary>
	private void DrawApplyToPrefabCopiesConfirmationPopup()
	{
		if (_showApplyToPrefabCopiesConfirmation)
		{
			ImGui.OpenPopup("apply-to-prefab-copies-confirmation");
			_showApplyToPrefabCopiesConfirmation = false; // Only open once
		}

		// Center the popup when it first appears
		var center = new Num.Vector2(Screen.Width * 0.45f, Screen.Height * 0.45f);
		ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Num.Vector2(0.5f, 0.5f));
		ImGui.SetNextWindowSize(new Num.Vector2(450, 0), ImGuiCond.Appearing);

		bool open = true;
		if (Gui.BeginPopupModal("apply-to-prefab-copies-confirmation", ref open, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar))
		{
			ImGui.Text("Apply Changes to SerializedPrefab Copies");
			ImGui.Separator();
			
			ImGuiSafe.TextWrappedSafe($"You are going to change prefab values for these entities:");
			
			VoltageEditorUtils.SmallVerticalSpace();
			
			// Show the list of entities that will be affected
			ImGuiSafe.TextColoredSafe(new Num.Vector4(1.0f, 0.8f, 0.2f, 1.0f), $"SerializedPrefab: {Entity.OriginalPrefabName}");
			ImGuiSafe.TextColoredSafe(new Num.Vector4(0.8f, 0.8f, 0.8f, 1.0f), $"Entities to be modified ({_prefabCopiesToModify.Count}):");

			// Create a scrollable region for the entity list
			if (ImGui.BeginChild("EntityList", new Num.Vector2(0, Math.Min(200, _prefabCopiesToModify.Count * 80 + 20)), true))
			{
				foreach (var prefabCopy in _prefabCopiesToModify)
				{
					ImGuiSafe.BulletTextSafe($"{prefabCopy.Name}");
					ImGui.Dummy(new Num.Vector2(0, 2)); // spacing between items
				}
			}
			ImGui.EndChild();

			VoltageEditorUtils.MediumVerticalSpace();
			
			ImGui.TextColored(new Num.Vector4(1.0f, 0.6f, 0.2f, 1.0f), "This action can be undone with Ctrl+Z");

			VoltageEditorUtils.MediumVerticalSpace();

			// Center the buttons
			var buttonWidth = 80f;
			var spacing = 10f;
			var totalButtonWidth = buttonWidth * 2 + spacing;
			var windowWidth = ImGui.GetWindowSize().X;
			var centerStart = (windowWidth - totalButtonWidth) * 0.5f;
			
			ImGui.SetCursorPosX(centerStart);
			
			if (Gui.Button("OK", new Num.Vector2(buttonWidth, 0)))
			{
				// Proceed with applying changes
				ApplyToPrefabCopies(Entity);
				ImGui.CloseCurrentPopup();
			}
			
			ImGui.SameLine();
			
			if (Gui.Button("Cancel", new Num.Vector2(buttonWidth, 0)))
			{
				// Clear the list and close popup
				_prefabCopiesToModify.Clear();
				ImGui.CloseCurrentPopup();
			}

			Gui.EndPopup();
		}
	}

	/// <summary>
	/// Draws the confirmation popup for applying changes to the original prefab.
	/// </summary>
	private void DrawApplyToOriginalPrefabConfirmationPopup()
	{
		if (_showApplyToOriginalPrefabConfirmation)
		{
			ImGui.OpenPopup("apply-to-original-prefab-confirmation");
			_showApplyToOriginalPrefabConfirmation = false; // Only open once
		}

		// Center the popup when it first appears
		var center = new Num.Vector2(Screen.Width * 0.5f, Screen.Height * 0.5f);
		ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Num.Vector2(0.5f, 0.5f));
		ImGui.SetNextWindowSize(new Num.Vector2(400, 0), ImGuiCond.Appearing);

		bool open = true;
		if (Gui.BeginPopupModal("apply-to-original-prefab-confirmation", ref open, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar))
		{
			ImGuiSafe.TextColoredSafe(new Num.Vector4(1.0f, 0.6f, 0.2f, 1.0f), $"Are you sure you want to override the data of '{Entity.OriginalPrefabName}'?");
			ImGui.Separator();
			ImGui.TextWrapped("This action will overwrite the original prefab file and cannot be undone outside of this session.");

			VoltageEditorUtils.MediumVerticalSpace();

			// Center the buttons
			var buttonWidth = 80f;
			var spacing = 10f;
			var totalButtonWidth = buttonWidth * 2 + spacing;
			var windowWidth = ImGui.GetWindowSize().X;
			var centerStart = (windowWidth - totalButtonWidth) * 0.5f;
			
			ImGui.SetCursorPosX(centerStart);

			if (Gui.Button("Yes", new Num.Vector2(buttonWidth, 0)))
			{
				ApplyToOriginalPrefabWithUndo();
				ImGui.CloseCurrentPopup();
			}

			ImGui.SameLine();

			if (Gui.Button("No", new Num.Vector2(buttonWidth, 0)))
			{
				ImGui.CloseCurrentPopup();
			}

			Gui.EndPopup();
		}
	}

	private async void ApplyToOriginalPrefabWithUndo()
	{
		// Save the current entity's data to its original prefab file
		if (Entity != null && Entity.Type == Entity.InstanceType.SerializedPrefab && !string.IsNullOrEmpty(Entity.OriginalPrefabName))
		{
			// Save the prefab using the async event system
			bool saveSuccessful = await SerializationManager.Instance.InvokePrefabCreated(Entity, true);

			if (saveSuccessful)
			{
				EditorDebug.Log($"Applied changes to original prefab: {Entity.OriginalPrefabName}");
			}
			else
			{
				EditorDebug.Log($"Failed to apply changes to prefab: {Entity.OriginalPrefabName}");
			}
		}
	}

	public void SetWindowFocus()
	{
		_shouldFocusWindow = true;
	}

	// Phase 4b — Prefab override panel

	/// <summary>
	/// Gets the <see cref="Voltage.Data.SceneData.SceneEntityData"/> for the current entity
	/// from the active scene's SceneData, or null when not present.
	/// This is the authoritative source of override metadata (<see cref="Voltage.Data.SceneData.SceneEntityData.PrefabOverrides"/>).
	/// </summary>
	private Voltage.Data.SceneData.SceneEntityData GetCurrentSceneEntityData()
	{
		var sceneData = Core.Scene?.SceneData;
		if (sceneData?.Entities == null || Entity == null)
			return null;

		return sceneData.Entities.FirstOrDefault(e =>
			string.Equals(e.Name, Entity.Name, StringComparison.OrdinalIgnoreCase));
	}

	/// <summary>
	/// Draws the Phase 4b override summary panel for a <see cref="Entity.InstanceType.SerializedPrefab"/> entity.
	/// Shows which components are overridden / removed relative to the source prefab and provides
	/// per-component and all-at-once revert actions.
	/// </summary>
	private void DrawPrefabOverridePanel()
	{
		var entityData = GetCurrentSceneEntityData();
		if (entityData == null)
			return;

		var overrides = entityData.PrefabOverrides;
		var removed   = entityData.RemovedPrefabComponents;

		// Legacy full-data entry (pre-Phase-4b) — no override metadata to show.
		if (overrides == null)
		{
			ImGui.PushStyleColor(ImGuiCol.Text, new Num.Vector4(0.7f, 0.7f, 0.7f, 1f));
			ImGui.TextWrapped("(Legacy entry — no override tracking. Re-save scene to enable delta model.)");
			ImGui.PopStyleColor();
			return;
		}

		var totalOverrides = overrides.Count + (removed?.Count ?? 0);

		if (totalOverrides == 0)
		{
			ImGui.PushStyleColor(ImGuiCol.Text, new Num.Vector4(0.5f, 0.9f, 0.5f, 1f));
			ImGui.Text("No overrides — all components match prefab.");
			ImGui.PopStyleColor();
			return;
		}

		// Header with count.
		ImGui.PushStyleColor(ImGuiCol.Text, new Num.Vector4(1f, 0.75f, 0.2f, 1f));
		ImGuiSafe.TextSafe($"Overrides: {totalOverrides} component(s)");
		ImGui.PopStyleColor();

		VoltageEditorUtils.SmallVerticalSpace();

		// Per-component list.
		if (ImGui.BeginChild("##PrefabOverrideList",
			new Num.Vector2(0, Math.Min(150, (totalOverrides + 1) * 22 + 10)), true))
		{
			// Overridden / added components.
			foreach (var name in overrides)
			{
				ImGui.PushStyleColor(ImGuiCol.Text, new Num.Vector4(1f, 0.85f, 0.3f, 1f));
				ImGui.Bullet();
				ImGui.SameLine();
				ImGuiSafe.TextSafe(name);
				ImGui.PopStyleColor();

				ImGui.SameLine(ImGui.GetContentRegionAvail().X - 60f);
				if (Gui.SmallButton($"Revert##{name}"))
					_revertComponentName = name;

				if (ImGui.IsItemHovered())
					ImGuiSafe.SetTooltipSafe($"Revert '{name}' to the prefab's version.");
			}

			// Removed components.
			if (removed != null)
			{
				foreach (var name in removed)
				{
					ImGui.PushStyleColor(ImGuiCol.Text, new Num.Vector4(1f, 0.4f, 0.4f, 1f));
					ImGui.Bullet();
					ImGui.SameLine();
					ImGuiSafe.TextSafe($"{name}  (removed)");
					ImGui.PopStyleColor();

					ImGui.SameLine(ImGui.GetContentRegionAvail().X - 60f);
					if (Gui.SmallButton($"Restore##{name}"))
						_revertComponentName = name;

					if (ImGui.IsItemHovered())
						ImGuiSafe.SetTooltipSafe($"Restore '{name}' from the prefab.");
				}
			}
		}
		ImGui.EndChild();

		VoltageEditorUtils.SmallVerticalSpace();

		if (VoltageEditorUtils.CenteredButton("Revert All Overrides to Prefab", 0.8f))
			_showRevertAllOverridesConfirmation = true;
	}

	/// <summary>
	/// Draws the confirmation popup for reverting ALL overrides on the entity.
	/// </summary>
	private void DrawRevertAllOverridesConfirmationPopup()
	{
		if (_showRevertAllOverridesConfirmation)
		{
			ImGui.OpenPopup("revert-all-overrides-confirmation");
			_showRevertAllOverridesConfirmation = false;
		}

		var center = new Num.Vector2(Screen.Width * 0.5f, Screen.Height * 0.4f);
		ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Num.Vector2(0.5f, 0.5f));
		ImGui.SetNextWindowSize(new Num.Vector2(420, 0), ImGuiCond.Appearing);

		bool open = true;
		if (Gui.BeginPopupModal("revert-all-overrides-confirmation", ref open,
			ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar))
		{
			ImGui.TextColored(new Num.Vector4(1f, 0.6f, 0.2f, 1f),
				"Revert all component overrides to prefab?");
			ImGui.Separator();
			ImGuiSafe.TextWrappedSafe(
				$"This will discard all overrides on '{Entity?.Name}' and restore it to the " +
				$"current '{Entity?.OriginalPrefabName}' prefab state. The entity position and " +
				"rotation are NOT affected.");

			VoltageEditorUtils.MediumVerticalSpace();

			var bw = 80f;
			ImGui.SetCursorPosX((ImGui.GetWindowSize().X - bw * 2 - 10f) * 0.5f);

			if (Gui.Button("Revert", new Num.Vector2(bw, 0)))
			{
				RevertAllOverrides();
				ImGui.CloseCurrentPopup();
			}

			ImGui.SameLine();

			if (Gui.Button("Cancel", new Num.Vector2(bw, 0)))
				ImGui.CloseCurrentPopup();

			Gui.EndPopup();
		}
	}

	/// <summary>
	/// Draws the confirmation popup for reverting a single component override.
	/// </summary>
	private void DrawRevertComponentOverrideConfirmationPopup()
	{
		if (_revertComponentName != null)
		{
			ImGui.OpenPopup("revert-component-override-confirmation");
		}

		var center = new Num.Vector2(Screen.Width * 0.5f, Screen.Height * 0.4f);
		ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Num.Vector2(0.5f, 0.5f));
		ImGui.SetNextWindowSize(new Num.Vector2(420, 0), ImGuiCond.Appearing);

		bool open = true;
		if (Gui.BeginPopupModal("revert-component-override-confirmation", ref open,
			ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar))
		{
			var compName = _revertComponentName ?? "(unknown)";
			ImGuiSafe.TextColoredSafe(new Num.Vector4(1f, 0.6f, 0.2f, 1f),
				$"Revert '{compName}' to prefab version?");
			ImGui.Separator();
			ImGuiSafe.TextWrappedSafe(
				$"The component '{compName}' on '{Entity?.Name}' will be restored to the " +
				$"'{Entity?.OriginalPrefabName}' prefab's version.  If the component was removed " +
				"on this instance it will be re-added from the prefab.");

			VoltageEditorUtils.MediumVerticalSpace();

			var bw = 80f;
			ImGui.SetCursorPosX((ImGui.GetWindowSize().X - bw * 2 - 10f) * 0.5f);

			if (Gui.Button("Revert", new Num.Vector2(bw, 0)))
			{
				RevertComponentOverride(_revertComponentName);
				_revertComponentName = null;
				ImGui.CloseCurrentPopup();
			}

			ImGui.SameLine();

			if (Gui.Button("Cancel", new Num.Vector2(bw, 0)))
			{
				_revertComponentName = null;
				ImGui.CloseCurrentPopup();
			}

			Gui.EndPopup();
		}
	}

	/// <summary>
	/// Reverts all component overrides on the current entity by clearing the override metadata
	/// and reloading component data from the source prefab.
	/// </summary>
	private void RevertAllOverrides()
	{
		if (Entity == null || Entity.Type != Entity.InstanceType.SerializedPrefab)
			return;

		var entitySceneData = GetCurrentSceneEntityData();
		if (entitySceneData == null)
			return;

		var prefabData = TryLoadPrefabForRevert(Entity.OriginalPrefabName, Entity.OriginalPrefabGuid);
		if (prefabData.HasValue)
		{
			PrefabOverrides.RevertAll(Entity, entitySceneData, prefabData.Value);
			EditorDebug.Log($"Reverted all overrides on '{Entity.Name}' to prefab '{Entity.OriginalPrefabName}'.");
		}
		else
		{
			EditorDebug.Error($"Could not load prefab '{Entity.OriginalPrefabName}' for revert.");
		}

		DelayedSetEntity(Entity);
	}

	/// <summary>
	/// Reverts a single component override by name, restoring it from the source prefab.
	/// </summary>
	private void RevertComponentOverride(string componentName)
	{
		if (Entity == null || string.IsNullOrEmpty(componentName))
			return;

		var entitySceneData = GetCurrentSceneEntityData();
		if (entitySceneData == null)
			return;

		var prefabData = TryLoadPrefabForRevert(Entity.OriginalPrefabName, Entity.OriginalPrefabGuid);
		if (!prefabData.HasValue)
		{
			EditorDebug.Error($"Could not load prefab '{Entity.OriginalPrefabName}' for component revert.");
			return;
		}

		PrefabOverrides.RevertComponent(Entity, entitySceneData, prefabData.Value, componentName);
		EditorDebug.Log($"Reverted '{componentName}' on '{Entity.Name}' to prefab version.");
		DelayedSetEntity(Entity);
	}

	/// <summary>
	/// Loads the source prefab for revert operations. Uses <see cref="SerializationManager"/> path.
	/// </summary>
	private static PrefabData? TryLoadPrefabForRevert(string prefabName, Guid prefabGuid)
	{
		if (string.IsNullOrEmpty(prefabName))
			return null;

		try
		{
			// Use the engine-side resolver (wired in ProjectManager) for consistency.
			var path = Scene.PrefabPathResolver?.Invoke(prefabGuid, prefabName);
			if (string.IsNullOrEmpty(path) || !File.Exists(path))
				return null;

			var json = File.ReadAllText(path);
			var pd = Voltage.Serialization.AotDeserializers.DeserializePrefabData(json);
			return pd.Name != null ? pd : (PrefabData?)null;
		}
		catch
		{
			return null;
		}
	}

}