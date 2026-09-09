using System;
using System.Collections.Generic;
using ImGuiNET;
using Voltage.Editor.Utils;
using Num = System.Numerics;

namespace Voltage.Editor.Hotkeys
{
	/// <summary>"Custom Hotkeys" section of the Editor Settings window: per-category rebinding with clash checks.</summary>
	public static class HotkeySettingsSection
	{
		private static HotkeyAction _capturing;
		private static bool _capturingAlternate;
		private static HotkeyAction _pendingAction;
		private static bool _pendingAlternate;
		private static HotkeyBinding _pendingBinding;
		private static HotkeyAction _conflictWith;
		private static bool _openConflictPopup;

		private static bool _sawModifiers;

		public static void Draw()
		{
			if (Gui.CollapsingHeader("Custom Hotkeys"))
			{
				ImGui.Indent();
				VoltageEditorUtils.SmallVerticalSpace();

				ImGui.TextDisabled("Click a shortcut to rebind it, then press the new combination. Esc cancels.");
				VoltageEditorUtils.SmallVerticalSpace();

				if (Gui.Button("Reset all to defaults"))
					EditorHotkeys.ResetAllToDefaults();

				VoltageEditorUtils.SmallVerticalSpace();

				foreach (var category in EditorHotkeys.Categories())
					DrawCategory(category);

				VoltageEditorUtils.SmallVerticalSpace();
				ImGui.Unindent();
			}

			// Pumped even when the header is collapsed, or an armed capture would never resolve.
			CaptureKeyIfListening();
			DrawConflictPopup();
			HotkeyErrorPopup.Draw();
		}

		private static void DrawCategory(string category)
		{
			if (!Gui.TreeNodeEx(category, ImGuiTreeNodeFlags.DefaultOpen))
				return;

			if (ImGui.BeginTable($"hotkeys-{category}", 4,
				    ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV))
			{
				ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthStretch, 0.36f);
				ImGui.TableSetupColumn("Shortcut", ImGuiTableColumnFlags.WidthStretch, 0.26f);
				ImGui.TableSetupColumn("Alternate", ImGuiTableColumnFlags.WidthStretch, 0.26f);
				ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthStretch, 0.12f);
				ImGui.TableHeadersRow();

				foreach (var action in EditorHotkeys.InCategory(category))
					DrawRow(action);

				ImGui.EndTable();
			}

			ImGui.TreePop();
		}

		private static void DrawRow(HotkeyAction action)
		{
			ImGui.TableNextRow();

			ImGui.TableNextColumn();
			ImGui.TextUnformatted(action.Label);
			if (ImGui.IsItemHovered() && !string.IsNullOrEmpty(action.Tooltip))
				ImGui.SetTooltip(action.Tooltip);

			ImGui.TableNextColumn();
			DrawBindButton(action, alternate: false);

			ImGui.TableNextColumn();
			DrawBindButton(action, alternate: true);

			ImGui.TableNextColumn();
			ImGui.BeginDisabled(action.IsDefault);
			if (Gui.SmallButton($"Reset##{action.Id}"))
				action.ResetToDefault();
			ImGui.EndDisabled();

			if (ImGui.IsItemHovered() && !action.IsDefault)
				ImGui.SetTooltip($"Back to {action.DefaultPrimary.ToDisplayString()}");
		}

		private static void DrawBindButton(HotkeyAction action, bool alternate)
		{
			var capturing = ReferenceEquals(_capturing, action) && _capturingAlternate == alternate;
			var label = capturing ? "Press a key..." : action.Get(alternate).ToDisplayString();

			if (capturing)
				ImGui.PushStyleColor(ImGuiCol.Button, new Num.Vector4(0.2f, 0.5f, 1f, 1f));

			if (Gui.Button($"{label}##bind{action.Id}{alternate}", new Num.Vector2(-1, 0)))
			{
				SetCapturing(capturing ? null : action, alternate);
			}

			if (capturing)
				ImGui.PopStyleColor();

			if (!capturing && ImGui.IsItemHovered())
				ImGui.SetTooltip("Click to rebind. Right-click to clear.");

			if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
				action.Rebind(HotkeyBinding.None, alternate);
		}

		/// <summary>Reads the next non-modifier key while a row is armed and routes it through the clash check.</summary>
		private static void CaptureKeyIfListening()
		{
			if (_capturing == null)
				return;

			if (ImGui.IsKeyPressed(ImGuiKey.Escape, false))
			{
				SetCapturing(null, false);
				return;
			}

			var io = ImGui.GetIO();
			var modifiersHeld = io.KeyCtrl || io.KeyShift || io.KeyAlt || io.KeySuper;

			if (modifiersHeld)
			{
				_sawModifiers = true;
			}
			else if (_sawModifiers)
			{
				// Modifiers went back up without a real key, so there is nothing to bind.
				SetCapturing(null, false);
				HotkeyErrorPopup.Show(
					"A shortcut needs a normal key, not just modifiers.\n\n" +
					"Hold Ctrl, Shift or Alt and then press a letter, number or function key " +
					"— for example Ctrl+Shift+Z.");
				return;
			}

			foreach (var key in AllKeys())
			{
				if (!ImGui.IsKeyPressed(key, false) || IsModifier(key))
					continue;

				var binding = new HotkeyBinding(key, io.KeyCtrl || io.KeySuper, io.KeyShift, io.KeyAlt);
				var conflict = EditorHotkeys.FindConflict(_capturing, binding);

				if (conflict != null)
				{
					_pendingAction = _capturing;
					_pendingAlternate = _capturingAlternate;
					_pendingBinding = binding;
					_conflictWith = conflict;
					_openConflictPopup = true;
				}
				else
				{
					_capturing.Rebind(binding, _capturingAlternate);
				}

				SetCapturing(null, false);
				return;
			}
		}

		/// <summary>Arming capture also mutes every live binding so the typed combo does not trigger its action.</summary>
		private static void SetCapturing(HotkeyAction action, bool alternate)
		{
			_capturing = action;
			_capturingAlternate = alternate;
			_sawModifiers = false;
			EditorHotkeys.CaptureMode = action != null;
		}

		private static void DrawConflictPopup()
		{
			if (_openConflictPopup)
			{
				ImGui.OpenPopup("HotkeyConflictPopup");
				_openConflictPopup = false;
			}

			var center = ImGui.GetMainViewport().GetCenter();
			ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Num.Vector2(0.5f, 0.5f));

			bool open = true;
			if (!Gui.BeginPopupModal("HotkeyConflictPopup", ref open, ImGuiWindowFlags.AlwaysAutoResize))
			{
				if (!open)
					ClearPending();

				return;
			}

			ImGuiSafe.TextColoredSafe(new Num.Vector4(1f, 0.75f, 0.2f, 1f), "Shortcut already in use");
			ImGui.Separator();

			ImGui.PushTextWrapPos(400f);
			ImGuiSafe.TextWrappedSafe(
				$"{_pendingBinding.ToDisplayString()} already runs \"{_conflictWith?.Label}\" " +
				$"under {_conflictWith?.Category}.");

			ImGuiSafe.TextWrappedSafe(
				$"Reassigning it to \"{_pendingAction?.Label}\" will leave \"{_conflictWith?.Label}\" " +
				"with no shortcut until you give it another one.");
			ImGui.PopTextWrapPos();

			VoltageEditorUtils.MediumVerticalSpace();

			if (Gui.Button("Reassign", new Num.Vector2(110, 0)))
			{
				ClearConflictingBinding();
				_pendingAction?.Rebind(_pendingBinding, _pendingAlternate);
				ClearPending();
				ImGui.CloseCurrentPopup();
			}

			ImGui.SameLine();

			if (Gui.Button("Cancel", new Num.Vector2(110, 0)))
			{
				ClearPending();
				ImGui.CloseCurrentPopup();
			}

			Gui.EndPopup();
		}

		/// <summary>Frees whichever slot of the other action held the combo we are taking over.</summary>
		private static void ClearConflictingBinding()
		{
			if (_conflictWith == null)
				return;

			if (_conflictWith.Primary == _pendingBinding)
				_conflictWith.Rebind(HotkeyBinding.None);

			if (_conflictWith.Alternate == _pendingBinding)
				_conflictWith.Rebind(HotkeyBinding.None, alternate: true);
		}

		private static void ClearPending()
		{
			_pendingAction = null;
			_conflictWith = null;
			_pendingBinding = HotkeyBinding.None;
		}

		/// <summary>
		/// Excludes the Left/Right modifier keys and ImGui's ReservedForMod* pseudo-keys, which otherwise capture
		/// as a bindable key and produce nonsense like "Ctrl+ReservedForModCtrl".
		/// </summary>
		private static bool IsModifier(ImGuiKey key)
		{
			var name = key.ToString();

			return name.Contains("Ctrl", StringComparison.Ordinal) ||
			       name.Contains("Shift", StringComparison.Ordinal) ||
			       name.Contains("Alt", StringComparison.Ordinal) ||
			       name.Contains("Super", StringComparison.Ordinal) ||
			       name.StartsWith("Mod", StringComparison.Ordinal) ||
			       name.StartsWith("Reserved", StringComparison.Ordinal);
		}

		private static IEnumerable<ImGuiKey> AllKeys()
		{
			for (var key = ImGuiKey.NamedKey_BEGIN; key < ImGuiKey.NamedKey_END; key++)
				yield return key;
		}
	}
}
