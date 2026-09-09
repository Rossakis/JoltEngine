using System;
using System.Collections.Generic;
using ImGuiNET;
using Voltage.Project;

namespace Voltage.Editor.Inspectors.TypeInspectors
{
	/// <summary>
	/// Renders a single-select combo for a physics layer identity field (e.g. Collider.PhysicsLayer).
	/// The stored value is 1 shifted left by the layer's index (e.g. 1 &lt;&lt; 0).
	/// </summary>
	public class PhysicsLayerTypeInspector : AbstractTypeInspector
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
			var currentValue = GetValue<int>();
			var selectedIndex = Array.IndexOf(_layerBitValues, currentValue);
			if (selectedIndex < 0)
				selectedIndex = 0;

			if (Gui.Combo(_name, ref selectedIndex, _layerNames, _layerNames.Length))
				SetValueWithUndo(_layerBitValues[selectedIndex], _name);

			HandleTooltip();
		}
	}
}