using System;
using System.Collections.Generic;
using ImGuiNET;
using Voltage.Project;

namespace Voltage.Editor.Inspectors.TypeInspectors
{
	/// <summary>
	/// Renders a combo dropdown for a render layer (value stored directly as the int defined in ProjectSettings).
	/// </summary>
	public class RenderLayerTypeInspector : AbstractTypeInspector
	{
		private string[] _layerNames;
		private int[] _layerValues;

		public override void Initialize()
		{
			base.Initialize();
			RefreshLayers();
		}

		private void RefreshLayers()
		{
			var layers = ProjectSettings.Instance.Rendering.RenderingLayers;
			var names = new List<string>();
			var values = new List<int>();

			foreach (var kvp in layers)
			{
				names.Add(kvp.Key);
				values.Add(kvp.Value);
			}

			_layerNames = names.ToArray();
			_layerValues = values.ToArray();
		}

		public override void DrawMutable()
		{
			var currentValue = GetValue<int>();
			var selectedIndex = Array.IndexOf(_layerValues, currentValue);
			if (selectedIndex < 0)
				selectedIndex = 0;

			if (Gui.Combo(_name, ref selectedIndex, _layerNames, _layerNames.Length))
				SetValueWithUndo(_layerValues[selectedIndex], _name);

			HandleTooltip();
		}
	}
}