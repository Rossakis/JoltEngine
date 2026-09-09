using System;
using ImGuiNET;
using Voltage.Editor.Tools;
using Voltage.Utils;
using Num = System.Numerics;

namespace Voltage.Editor.Utils
{
	/// <summary>
	/// Represents the progress state of a compilation operation
	/// </summary>
	public class CompilationProgress
	{
		public string Title { get; set; }
		public int TotalItems { get; set; }
		public int CompletedItems { get; set; }
		public int SuccessCount { get; set; }
		public int FailureCount { get; set; }
		public string CurrentItem { get; set; }
		public bool IsComplete { get; set; }
		public string CompletionMessage { get; set; }
		
		public float Progress => TotalItems > 0 ? (float)CompletedItems / TotalItems : 0f;
	}

	/// <summary>
	/// Reusable compilation progress window for displaying build/compile operations
	/// </summary>
	public class CompilationProgressWindow
	{
		private bool _isVisible = false;
		private CompilationProgress _currentProgress;
		private readonly bool _isScriptProgress;
		private bool _hasClosedAfterCompletion = false;
		private readonly Action _onCancel;

		public bool IsVisible => _isVisible;

		public CompilationProgressWindow(bool isScriptProgress = false, Action onCancel = null)
		{
			_isScriptProgress = isScriptProgress;
			_onCancel = onCancel;
		}

		/// <summary>
		/// Shows the progress window with the given progress state
		/// </summary>
		public void Show(CompilationProgress progress)
		{
			_currentProgress = progress;
			_isVisible = true;
			_hasClosedAfterCompletion = false;
		}

		/// <summary>
		/// Hides the progress window
		/// </summary>
		public void Hide()
		{
			_isVisible = false;
			_currentProgress = null;
			_hasClosedAfterCompletion = false;
		}

		/// <summary>
		/// Draws the progress window
		/// </summary>
		public void Draw()
		{
			if (!_isVisible || _currentProgress == null)
				return;

			// Handle immediate auto-close when compilation completes
			if (_currentProgress.IsComplete && !_hasClosedAfterCompletion)
			{
				// Check the appropriate setting based on progress type
				bool shouldAutoClose = _isScriptProgress 
					? EditorSettingsWindow.AutoCloseScriptProgress 
					: EditorSettingsWindow.AutoCloseEffectsProgress;

				if (shouldAutoClose)
				{
					_hasClosedAfterCompletion = true;
					Hide();
					return;
				}
			}

			// Center the window
			var center = new Num.Vector2(Screen.Width * 0.5f, Screen.Height * 0.5f);
			ImGui.SetNextWindowPos(center, ImGuiCond.Always, new Num.Vector2(0.5f, 0.5f));
			ImGui.SetNextWindowSize(new Num.Vector2(500, 0), ImGuiCond.Always);

			var flags = ImGuiWindowFlags.NoResize | 
			            ImGuiWindowFlags.NoMove | 
			            ImGuiWindowFlags.NoCollapse |
			            ImGuiWindowFlags.NoTitleBar;

			if (Gui.Begin("CompilationProgress", flags))
			{
				DrawTitle();
				ImGui.Separator();
				
				VoltageEditorUtils.SmallVerticalSpace();
				
				DrawProgressBar();
				
				VoltageEditorUtils.SmallVerticalSpace();
				
				DrawCurrentItemStatus();
				
				VoltageEditorUtils.MediumVerticalSpace();
				
				DrawStatistics();
				
				if (_currentProgress.IsComplete)
				{
					VoltageEditorUtils.MediumVerticalSpace();
					DrawAutoCloseIndicator();
				}
				else if (_onCancel != null)
				{
					VoltageEditorUtils.MediumVerticalSpace();
					DrawCancelButton();
				}

				Gui.End();
			}
		}

		private void DrawCancelButton()
		{
			// Center the button
			var buttonWidth = 100f;
			var windowWidth = ImGui.GetWindowSize().X;
			ImGui.SetCursorPosX((windowWidth - buttonWidth) * 0.5f);
			
			if (Gui.Button("Cancel", new Num.Vector2(buttonWidth, 0)))
			{
				_onCancel?.Invoke();
				Hide();
			}
		}

		private void DrawTitle()
		{
			string statusIcon = _currentProgress.IsComplete 
				? (_currentProgress.FailureCount == 0 ? "[OK]" : "[X]") 
				: "[...]";

			var titleColor = _currentProgress.IsComplete
				? (_currentProgress.FailureCount == 0 
					? new Num.Vector4(0.4f, 0.8f, 0.4f, 1.0f) 
					: new Num.Vector4(0.8f, 0.4f, 0.4f, 1.0f))
				: new Num.Vector4(0.2f, 0.7f, 1.0f, 1.0f);

			ImGui.PushStyleColor(ImGuiCol.Text, titleColor);
			
			string title = _currentProgress.IsComplete
				? $"{statusIcon} {_currentProgress.Title} - Complete"
				: $"{statusIcon} {_currentProgress.Title}...";
			
			// Center the title
			var textSize = ImGui.CalcTextSize(title);
			var windowWidth = ImGui.GetWindowSize().X;
			ImGui.SetCursorPosX((windowWidth - textSize.X) * 0.5f);
			
			ImGuiSafe.TextSafe(title);
			ImGui.PopStyleColor();
		}

		private void DrawProgressBar()
		{
			var progress = _currentProgress.Progress;

			var progressColor = _currentProgress.IsComplete
				? (_currentProgress.FailureCount == 0
					? new Num.Vector4(0.4f, 0.8f, 0.4f, 1.0f)
					: new Num.Vector4(0.8f, 0.4f, 0.4f, 1.0f))
				: new Num.Vector4(0.2f, 0.7f, 1.0f, 1.0f);

			ImGui.PushStyleColor(ImGuiCol.PlotHistogram, progressColor);
			
			string progressText = $"{_currentProgress.CompletedItems}/{_currentProgress.TotalItems}";
			ImGui.ProgressBar(progress, new Num.Vector2(-1, 30), progressText);
			
			ImGui.PopStyleColor();
		}

		private void DrawCurrentItemStatus()
		{
			if (_currentProgress.IsComplete)
			{
				if (!string.IsNullOrWhiteSpace(_currentProgress.CompletionMessage))
				{
					var messageColor = _currentProgress.FailureCount == 0
						? new Num.Vector4(0.4f, 0.8f, 0.4f, 1.0f)
						: new Num.Vector4(0.8f, 0.4f, 0.4f, 1.0f);

					ImGui.PushStyleColor(ImGuiCol.Text, messageColor);
					ImGuiSafe.TextWrappedSafe(_currentProgress.CompletionMessage);
					ImGui.PopStyleColor();
				}
			}
			else if (!string.IsNullOrWhiteSpace(_currentProgress.CurrentItem))
			{
				ImGui.Text("Current:");
				ImGui.SameLine();
				ImGuiSafe.TextColoredSafe(new Num.Vector4(0.7f, 0.7f, 1.0f, 1.0f), _currentProgress.CurrentItem);
			}
		}

		private void DrawStatistics()
		{
			ImGui.BeginGroup();
			
			// Success count
			ImGui.TextColored(new Num.Vector4(0.4f, 0.8f, 0.4f, 1.0f), "[OK]");
			ImGui.SameLine();
			ImGuiSafe.TextSafe($"Success: {_currentProgress.SuccessCount}");
			
			ImGui.SameLine(0, 30);
			
			// Failure count
			if (_currentProgress.FailureCount > 0)
			{
				ImGui.TextColored(new Num.Vector4(0.8f, 0.4f, 0.4f, 1.0f), "[X]");
				ImGui.SameLine();
				ImGuiSafe.TextSafe($"Failed: {_currentProgress.FailureCount}");
			}
			else
			{
				ImGui.TextColored(new Num.Vector4(0.5f, 0.5f, 0.5f, 1.0f), "[X]");
				ImGui.SameLine();
				ImGuiSafe.TextDisabledSafe($"Failed: {_currentProgress.FailureCount}");
			}
			
			ImGui.EndGroup();
		}

		private void DrawAutoCloseIndicator()
		{
			// Check the appropriate setting based on progress type
			bool shouldAutoClose = _isScriptProgress 
				? EditorSettingsWindow.AutoCloseScriptProgress 
				: EditorSettingsWindow.AutoCloseEffectsProgress;

			if (!shouldAutoClose)
			{
				string message = "Auto-close disabled";
				var textSize = ImGui.CalcTextSize(message);
				var windowWidth = ImGui.GetWindowSize().X;
				ImGui.SetCursorPosX((windowWidth - textSize.X) * 0.5f);
				
				ImGuiSafe.TextDisabledSafe(message);
			}
		}
	}
}