using System;
using ImGuiNET;
using Microsoft.Xna.Framework.Graphics;
using Voltage.Utils;
using Voltage.Editor.ImGuiCore;
using Voltage.Editor.Utils;
using Num = System.Numerics;


namespace Voltage.Editor.Windows
{
	class CoreWindow
	{
		string[] _textureFilters;
		float[] _frameRateArray = new float[100];
		int _frameRateArrayIndex = 0;
		private ImGuiManager _imguiManager;

		public CoreWindow()
		{
			_textureFilters = Enum.GetNames(typeof(TextureFilter));
		}

		public bool Show(bool isOpen)
		{
			if (!isOpen)
				return false;

			if (_imguiManager == null)
				_imguiManager = Core.GetGlobalManager<ImGuiManager>();

			Gui.Begin("Core Settings###CoreWindow", ref isOpen, ImGuiWindowFlags.None);

			DrawSettings();
			Gui.End();

			return isOpen;
		}

		void DrawSettings()
		{
			_frameRateArray[_frameRateArrayIndex] = ImGui.GetIO().Framerate;
			_frameRateArrayIndex = (_frameRateArrayIndex + 1) % _frameRateArray.Length;

			ImGui.PlotLines("##hidelabel", ref _frameRateArray[0], _frameRateArray.Length, _frameRateArrayIndex,
				$"FPS: {ImGui.GetIO().Framerate:0}", 0, 60, new Num.Vector2(ImGui.GetContentRegionAvail().X, 50));

			VoltageEditorUtils.SmallVerticalSpace();

			if (Gui.CollapsingHeader("Core Settings", ImGuiTreeNodeFlags.DefaultOpen))
			{
				Gui.Checkbox("ResetSceneAutomatically", ref Core.ResetSceneAutomatically);
				Gui.Checkbox("exitOnEscapeKeypress", ref Core.ExitOnEscapeKeypress);
				Gui.Checkbox("pauseOnFocusLost", ref Core.PauseOnFocusLost);
				Gui.Checkbox("debugRenderEnabled", ref Core.DebugRenderEnabled);
			}

			if (Gui.CollapsingHeader("Core.defaultSamplerState", ImGuiTreeNodeFlags.DefaultOpen))
			{
				ImGui.PushStyleVar(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * 0.5f);
				VoltageEditorUtils.DisableNextWidget();

				var currentTextureFilter = (int) Core.DefaultSamplerState.Filter;
				if (Gui.Combo("Filter", ref currentTextureFilter, _textureFilters, _textureFilters.Length))
					Core.DefaultSamplerState.Filter = (TextureFilter) Enum.Parse(typeof(TextureFilter),
						_textureFilters[currentTextureFilter]);

				ImGui.PopStyleVar();
			}
		}
	}
}