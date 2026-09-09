using System;
using System.IO;
using ImGuiNET;
using Num = System.Numerics;

namespace Voltage.Editor.FilePickers
{
	/// <summary>
	/// File counterpart of <see cref="FolderBrowser"/>: native dialog when available, ImGui picker otherwise.
	/// Same Open / Draw / TryTakeResult contract.
	/// </summary>
	public sealed class AssetFileBrowser
	{
		private readonly string _popupId;
		private readonly string[] _extensions;
		private readonly string _filterDescription;

		private FilePicker _imguiPicker;
		private object _imguiOwner;
		private bool _openImguiNextFrame;
		private bool _imguiPopupWasVisible;
		private string _result;
		private bool _hasResult;

		/// <summary>True while the ImGui fallback popup is up. Always false on the native path.</summary>
		public bool IsBrowsing => _imguiPicker != null;

		/// <param name="extensions">Lower-case extensions with the leading dot, e.g. ".png".</param>
		public AssetFileBrowser(string popupId, string[] extensions, string filterDescription)
		{
			_popupId = popupId;
			_extensions = extensions ?? Array.Empty<string>();
			_filterDescription = filterDescription;
		}

		public void Open(string title, string startPath, object imguiOwner)
		{
			_result = null;
			_hasResult = false;

			var start = FolderBrowser.ResolveStart(startPath);

			if (NativeFileDialogs.IsAvailable)
			{
				var patterns = new string[_extensions.Length];
				for (var i = 0; i < _extensions.Length; i++)
					patterns[i] = "*" + _extensions[i];

				// Blocks here and returns immediately (empty on cancel).
				if (NativeFileDialogs.TryOpenFile(title, start, patterns, _filterDescription, out var picked)
				    && !string.IsNullOrEmpty(picked))
				{
					_result = picked;
					_hasResult = true;
				}

				return;
			}

			// The ImGui fallback filters on one extension only.
			_imguiOwner = imguiOwner ?? this;
			_imguiPicker = FilePicker.GetFilePicker(_imguiOwner, start,
				_extensions.Length > 0 ? _extensions[0] : null);
			_imguiPicker.DontAllowTraverselBeyondRootFolder = false;
			_openImguiNextFrame = true;
			_imguiPopupWasVisible = false;
		}

		public void Draw(string title = "Select File")
		{
			if (_imguiPicker == null)
				return;

			if (_openImguiNextFrame)
			{
				ImGui.OpenPopup(_popupId);
				_openImguiNextFrame = false;
			}

			var center = ImGui.GetMainViewport().GetCenter();
			ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Num.Vector2(0.5f, 0.5f));
			ImGui.SetNextWindowSize(new Num.Vector2(640, 520), ImGuiCond.Appearing);

			var open = true;
			if (Gui.BeginPopupModal(_popupId, ref open, ImGuiWindowFlags.NoResize))
			{
				_imguiPopupWasVisible = true;

				ImGui.TextColored(new Num.Vector4(0.2f, 0.8f, 1f, 1f), title);
				ImGui.Separator();

				if (_imguiPicker.Draw())
				{
					_result = _imguiPicker.SelectedFile;
					_hasResult = !string.IsNullOrEmpty(_result);
					CloseImgui();
					ImGui.CloseCurrentPopup();
				}

				Gui.EndPopup();
			}
			else if (_imguiPopupWasVisible)
			{
				// The picker's own Cancel button closes the popup without touching `open`.
				CloseImgui();
			}

			if (!open)
				CloseImgui();
		}

		public bool TryTakeResult(out string file)
		{
			file = _result;
			var had = _hasResult;
			_hasResult = false;
			_result = null;
			return had;
		}

		private void CloseImgui()
		{
			if (_imguiPicker != null)
				FilePicker.RemoveFilePicker(_imguiPicker);

			_imguiPicker = null;
			_imguiOwner = null;
			_imguiPopupWasVisible = false;
		}
	}
}
