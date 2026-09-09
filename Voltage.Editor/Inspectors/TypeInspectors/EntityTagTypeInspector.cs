using System;
using System.Collections.Generic;
using ImGuiNET;
using Voltage.Project;

namespace Voltage.Editor.Inspectors.TypeInspectors
{
	/// <summary>
	/// Renders a single-select combo for an entity tag field (e.g. Entity.Tag).
	/// The stored value is the direct int value defined in ProjectSettings.EntityTags.
	/// </summary>
	public class EntityTagTypeInspector : AbstractTypeInspector
	{
		private string[] _tagNames;
		private int[] _tagValues;

		public override void Initialize()
		{
			base.Initialize();
			RefreshTags();
		}

		private void RefreshTags()
		{
			var tags = ProjectSettings.Instance.Entities.EntityTags;
			var names = new List<string>();
			var values = new List<int>();

			foreach (var kvp in tags)
			{
				names.Add(kvp.Key);
				values.Add(kvp.Value);
			}

			_tagNames = names.ToArray();
			_tagValues = values.ToArray();
		}

		public override void DrawMutable()
		{
			var currentValue = GetValue<int>();
			var selectedIndex = Array.IndexOf(_tagValues, currentValue);
			if (selectedIndex < 0)
				selectedIndex = 0;

			if (Gui.Combo(_name, ref selectedIndex, _tagNames, _tagNames.Length))
				SetValueWithUndo(_tagValues[selectedIndex], _name);

			HandleTooltip();
		}
	}
}