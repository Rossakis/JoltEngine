using System.Collections.Generic;
using ImGuiNET;
using Voltage.Utils;
using Voltage.Editor.Inspectors.TypeInspectors;
using Voltage.Editor.Utils;
using EffectInspector = Voltage.Editor.Inspectors.TypeInspectors.EffectInspector;


namespace Voltage.Editor.Inspectors.ObjectInspectors
{
	public class PostProcessorInspector
	{
		public PostProcessor PostProcessor => _postProcessor;

		protected List<AbstractTypeInspector> _inspectors;
		protected int _scopeId = VoltageEditorUtils.GetScopeId();

		PostProcessor _postProcessor;

		public PostProcessorInspector(PostProcessor postProcessor)
		{
			_postProcessor = postProcessor;
			_inspectors = TypeInspectorUtils.GetInspectableProperties(postProcessor);

			// if we are a Material<T>, we need to fix the duplicate Effect due to the "new T effect"
			if (ReflectionUtils.IsGenericTypeOrSubclassOfGenericType(_postProcessor.GetType()))
			{
				var didFindEffectInspector = false;
				for (var i = 0; i < _inspectors.Count; i++)
				{
					var isEffectInspector = _inspectors[i] is EffectInspector;
					if (isEffectInspector)
					{
						if (didFindEffectInspector)
						{
							_inspectors.RemoveAt(i);
							break;
						}

						didFindEffectInspector = true;
					}
				}
			}

			for (var i = 0; i < _inspectors.Count; i++)
			{
				var effectInspector = _inspectors[i] as TypeInspectors.EffectInspector;
				if (effectInspector != null)
					effectInspector.AllowsEffectRemoval = false;
			}
		}

		public void Draw()
		{
			ImGui.PushID(_scopeId);
			var isOpen = Gui.CollapsingHeader(_postProcessor.GetType().Name.Replace("PostProcessor", string.Empty));

			VoltageEditorUtils.ShowContextMenuTooltip();

			if (Gui.BeginPopupContextItem())
			{
				if (Gui.Selectable("Remove PostProcessor"))
				{
					isOpen = false;
					Core.Scene.RemovePostProcessor(_postProcessor);
					ImGui.CloseCurrentPopup();
				}

				Gui.EndPopup();
			}

			if (isOpen)
			{
				ImGui.Indent();
				foreach (var inspector in _inspectors)
					inspector.Draw();
				ImGui.Unindent();
			}

			ImGui.PopID();
		}
	}
}