using System.Collections.Generic;
using ImGuiNET;
using Voltage.Editor.Utils;
using Voltage.Project;

namespace Voltage.Editor.Inspectors.TypeInspectors
{
	/// <summary>
	/// Renders a collapsible checklist for a physics layer bitmask (e.g. Collider.CollidesWithLayers).
	/// Multiple layers can be selected simultaneously.
	/// </summary>
	public class PhysicsLayerMaskTypeInspector : AbstractTypeInspector
	{
		private string[] _layerNames;
		private int[] _layerBitValues;

		public override void Initialize()
		{
			base.Initialize();
			RefreshLayers();
		}

		private void RefreshLayers()
		{
			var layers = ProjectSettings.Instance.Physics.PhysicsLayers;
			var names = new List<string>();
			var bits = new List<int>();

			foreach (var kvp in layers)
			{
				names.Add(kvp.Key);
				bits.Add(1 << kvp.Value);
			}

			_layerNames = names.ToArray();
			_layerBitValues = bits.ToArray();
		}

		public override void DrawMutable()
		{
			DrawTree(isReadOnly: false);
		}

		/// <summary>
		/// Allows the tree node to open/close freely, but disables all checkboxes inside.
		/// </summary>
		public override void DrawReadOnly()
		{
			DrawTree(isReadOnly: true);
		}

		private void DrawTree(bool isReadOnly)
		{
			var currentMask = GetValue<int>();
			var newMask = currentMask;

			var summary = BuildSummary(currentMask);

			VoltageEditorUtils.SmallVerticalSpace();

			if (Gui.TreeNode($"{_name}: {summary}"))
			{
				for (var i = 0; i < _layerNames.Length; i++)
				{
					var isChecked = currentMask == Physics.AllLayers || (currentMask & _layerBitValues[i]) != 0;

					if (isReadOnly)
					{
						// Disable only the checkboxes - tree remains interactive
						ImGui.BeginDisabled();
						Gui.Checkbox(_layerNames[i], ref isChecked);
						ImGui.EndDisabled();
					}
					else
					{
						if (Gui.Checkbox(_layerNames[i], ref isChecked))
						{
							if (isChecked)
								newMask |= _layerBitValues[i];
							else
								newMask &= ~_layerBitValues[i];
						}
					}
				}

				ImGui.TreePop();
			}

			VoltageEditorUtils.SmallVerticalSpace();

			if (!isReadOnly && newMask != currentMask)
				SetValueWithUndo(newMask, _name);

			HandleTooltip();
		}

		private string BuildSummary(int mask)
		{
			if (mask == Physics.AllLayers)
				return "All";

			if (mask == 0)
				return "None";

			var selected = new List<string>();
			for (var i = 0; i < _layerBitValues.Length; i++)
			{
				if ((mask & _layerBitValues[i]) != 0)
					selected.Add(_layerNames[i]);
			}

			return selected.Count > 0 ? string.Join(", ", selected) : "None";
		}
	}
}