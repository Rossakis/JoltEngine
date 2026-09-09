using System;
using System.IO;
using ImGuiNET;
using Num = System.Numerics;

namespace Voltage.Editor.FilePickers
{
	/// <summary>
	/// A folder chooser that uses the OS-native dialog when available (tinyfiledialogs) and transparently
	/// falls back to the editor's built-in ImGui folder picker otherwise. Usage from a window:
	/// <code>
	/// if (Gui.Button("Browse...")) _browser.Open("Select folder", currentPath, this);
	/// _browser.Draw();                                   // renders the ImGui fallback popup if active
	/// if (_browser.TryTakeResult(out var folder)) { ... } // fires once when a folder is chosen
	/// </code>
	/// The native dialog is modal and blocks inside <see cref="Open"/>, so the result is ready
	/// immediately in that path; the fallback resolves across frames via <see cref="Draw"/>.
	/// </summary>
	public sealed class FolderBrowser
	{
		private readonly string _popupId;
		private FilePicker _imguiPicker;
		private object _imguiOwner;
		private bool _openImguiNextFrame;
		private string _result;
		private bool _hasResult;

		public FolderBrowser(string popupId)
		{
			_popupId = popupId;
		}

		public void Open(string title, string startPath, object imguiOwner)
		{
			_result = null;
			_hasResult = false;

			var start = ResolveStart(startPath);

			if (NativeFileDialogs.IsAvailable)
			{
				// Native dialog blocks here and returns immediately (empty on cancel).
				if (NativeFileDialogs.TryPickFolder(title, start, out var picked) && !string.IsNullOrEmpty(picked))
				{
					_result = picked;
					_hasResult = true;
				}
				return;
			}

			_imguiOwner = imguiOwner ?? this;
			_imguiPicker = FilePicker.GetFolderPicker(_imguiOwner, start);
			_imguiPicker.DontAllowTraverselBeyondRootFolder = false;
			_openImguiNextFrame = true;
		}

		public void Draw(string title = "Select Folder")
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
			ImGui.SetNextWindowSize(new Num.Vector2(600, 500), ImGuiCond.Appearing);

			var open = true;
			if (Gui.BeginPopupModal(_popupId, ref open, ImGuiWindowFlags.NoResize))
			{
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

			if (!open)
				CloseImgui();
		}

		public bool TryTakeResult(out string folder)
		{
			folder = _result;
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
		}

		internal static string ResolveStart(string startPath)
		{
			if (!string.IsNullOrWhiteSpace(startPath))
			{
				if (Directory.Exists(startPath))
					return startPath;
				var parent = Path.GetDirectoryName(startPath);
				if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
					return parent;
			}
			return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
		}
	}
}
