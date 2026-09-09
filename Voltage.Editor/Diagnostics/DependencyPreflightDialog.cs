using System;
using System.Collections.Generic;
using System.Linq;
using ImGuiNET;
using Voltage.Diagnostics;
using Voltage.Editor.Utils;
using Num = System.Numerics;

namespace Voltage.Editor.Diagnostics
{
	/// <summary>
	/// ImGui modal that lists missing native dependencies, shows distro-aware manual install
	/// instructions, and (when supported) offers an opt-in "Install automatically" action that runs
	/// the install via the detected package manager behind a graphical polkit (pkexec) prompt.
	/// </summary>
	/// <remarks>
	/// Reused by the build-time preflight (blocking, before the AOT publish) and potentially the in-editor
	/// runtime warning. The dialog itself never installs anything unless the user clicks the button.
	/// Matches the existing Editor modal style (centered <c>BeginPopupModal</c>, colored section headers).
	/// </remarks>
	public sealed class DependencyPreflightDialog
	{
		private const string PopupId = "Missing Native Dependencies##DependencyPreflight";

		private bool _open;
		private bool _requestOpen;
		private string _title = "Missing Dependencies";
		private string _intro = "";
		private DependencyCheckResult _result;

		private bool _isInstalling;
		private string _statusMessage;
		private Num.Vector4 _statusColor = Colors.Info;

		// When true, the proceed button stays available even while deps are missing ("Build anyway"): the
		// preflight is advisory, and the real publish is the authority on whether the toolchain is usable.
		private bool _allowProceedWhenMissing;

		// When true, show a "Don't show this check again before building" checkbox.
		private bool _showSuppressOption;
		private bool _suppressChecked;

		// Callbacks
		private IReadOnlyList<NativeDependency> _dependencySet;
		private Action _onProceed;   // user wants to continue to the build (build flow only)
		private Action _onCancel;    // user backed out

		/// <summary>
		/// True when the user ticked "Don't show again" before closing. The caller reads this in its
		/// proceed/cancel callbacks to persist the preference. Reset each time the dialog is opened.
		/// </summary>
		public bool SuppressFutureChecksRequested => _suppressChecked;

		private static class Colors
		{
			public static readonly Num.Vector4 Header = new(0.2f, 0.8f, 1.0f, 1.0f);
			public static readonly Num.Vector4 Good = new(0.4f, 1.0f, 0.5f, 1.0f);
			public static readonly Num.Vector4 Bad = new(1.0f, 0.45f, 0.45f, 1.0f);
			public static readonly Num.Vector4 Warn = new(1.0f, 0.75f, 0.25f, 1.0f);
			public static readonly Num.Vector4 Info = new(0.7f, 0.7f, 0.7f, 1.0f);
		}

		/// <summary>True while the modal is showing.</summary>
		public bool IsOpen => _open || _requestOpen;

		/// <summary>
		/// Opens the dialog for the given check result.
		/// </summary>
		/// <param name="title">Window title text.</param>
		/// <param name="intro">A sentence explaining why this appeared (build vs runtime).</param>
		/// <param name="result">The dependency check result to display.</param>
		/// <param name="dependencySet">The dependency set, used for the "Recheck" button.</param>
		/// <param name="onProceed">Invoked if the user chooses to proceed to the build (optional; build flow).</param>
		/// <param name="onCancel">Invoked if the user cancels/closes.</param>
		/// <param name="allowProceedWhenMissing">
		/// When true the proceed button stays enabled even while deps are missing ("Build anyway"). Used by the
		/// advisory pre-build check so a detection miss never blocks a build that would otherwise succeed.
		/// </param>
		/// <param name="showSuppressOption">When true, show a "Don't show again" checkbox (build flow only).</param>
		public void Open(
			string title,
			string intro,
			DependencyCheckResult result,
			IReadOnlyList<NativeDependency> dependencySet,
			Action onProceed,
			Action onCancel,
			bool allowProceedWhenMissing = false,
			bool showSuppressOption = false)
		{
			_title = title;
			_intro = intro;
			_result = result;
			_dependencySet = dependencySet;
			_onProceed = onProceed;
			_onCancel = onCancel;
			_allowProceedWhenMissing = allowProceedWhenMissing;
			_showSuppressOption = showSuppressOption;
			_suppressChecked = false;
			_statusMessage = null;
			_isInstalling = false;
			_requestOpen = true;
		}

		/// <summary>
		/// Draws the modal. Call once per frame from the editor draw loop, after the dockspace.
		/// </summary>
		public void Draw()
		{
			if (_requestOpen)
			{
				ImGui.OpenPopup(PopupId);
				_requestOpen = false;
				_open = true;
			}

			if (!_open)
				return;

			var center = ImGui.GetMainViewport().GetCenter();
			ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Num.Vector2(0.5f, 0.5f));
			ImGui.SetNextWindowSize(new Num.Vector2(640, 0), ImGuiCond.Appearing);

			bool keepOpen = true;
			if (Gui.BeginPopupModal(PopupId, ref keepOpen, ImGuiWindowFlags.AlwaysAutoResize))
			{
				ImGuiSafe.TextColoredSafe(_result.AnyCriticalMissing ? Colors.Bad : Colors.Warn, _title);
				ImGui.Separator();
				ImGui.Spacing();

				ImGuiSafe.TextWrappedSafe(_intro);
				ImGui.Spacing();

				ImGui.Text("Detected system:");
				ImGui.SameLine();
				ImGuiSafe.TextColoredSafe(Colors.Info, $"{_result.DistroName} ({DescribePackageManager(_result.PackageManager)})");

				ImGui.Spacing();
				DrawDependencyTable();
				ImGui.Spacing();

				DrawManualInstructions();
				DrawManualOnlyDependencies();

				if (_result.ImmutableRoot)
				{
					ImGui.Spacing();
					ImGuiSafe.TextColoredSafe(Colors.Warn,
						"This system has a read-only root filesystem (SteamOS).");
					ImGuiSafe.TextWrappedSafe(
						"Automatic install is disabled. To install manually you must first unlock the root " +
						"filesystem, install, then re-lock it:");
					DrawCopyableCommand("sudo steamos-readonly disable");
					DrawCopyableCommand("# run the install command above");
					DrawCopyableCommand("sudo steamos-readonly enable");
				}

				if (!string.IsNullOrEmpty(_statusMessage))
				{
					ImGui.Spacing();
					ImGui.Separator();
					ImGuiSafe.TextColoredSafe(_statusColor, _statusMessage);
				}

				if (_showSuppressOption)
				{
					ImGui.Spacing();
					var suppress = _suppressChecked;
					if (Gui.Checkbox("Don't show this check again before building", ref suppress))
						_suppressChecked = suppress;
					if (ImGui.IsItemHovered())
						ImGuiSafe.SetTooltipSafe(
							"Skips this automatic check before each build. You can still verify the toolchain\n" +
							"anytime with the \"Check Dependencies\" button in the build window.");
				}

				ImGui.Spacing();
				ImGui.Separator();
				ImGui.Spacing();

				DrawActionButtons();

				Gui.EndPopup();
			}
			else if (_open)
			{
				// Popup closed via the window 'x' or Escape — treat as cancel.
				_open = false;
				_onCancel?.Invoke();
			}
		}

		private void DrawDependencyTable()
		{
			if (ImGui.BeginTable("##depTable", 2,
				    ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
			{
				ImGui.TableSetupColumn("Dependency", ImGuiTableColumnFlags.WidthStretch);
				ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 160f);
				ImGui.TableHeadersRow();

				foreach (var status in _result.Statuses)
				{
					ImGui.TableNextRow();

					ImGui.TableSetColumnIndex(0);
					ImGuiSafe.TextSafe(status.Dependency.FriendlyName);
					if (status.Dependency.Severity == DependencySeverity.Optional)
					{
						ImGui.SameLine();
						ImGui.TextColored(Colors.Info, "(optional)");
					}

					ImGui.TableSetColumnIndex(1);
					if (status.IsPresent)
						ImGui.TextColored(Colors.Good, "Installed");
					else if (status.Dependency.Severity == DependencySeverity.Optional)
						ImGui.TextColored(Colors.Warn, "Missing");
					else
						ImGui.TextColored(Colors.Bad, "MISSING");
				}

				ImGui.EndTable();
			}
		}

		private void DrawManualInstructions()
		{
			var packages = _result.MissingPackages();

			ImGui.TextColored(Colors.Header, "Manual installation");
			ImGui.Separator();
			ImGui.Spacing();

			if (_result.PackageManager == PackageManagerKind.None || packages.Count == 0)
			{
				ImGui.TextWrapped(
					"Your distribution's package manager could not be determined automatically. " +
					"Install the missing libraries listed above using your system's package manager.");
				return;
			}

			var manual = HostPackageManager.BuildManualInstallCommand(_result.PackageManager, packages);
			if (!string.IsNullOrEmpty(manual))
			{
				ImGui.TextWrapped("Run this to install the missing dependencies:");
				ImGui.Spacing();
				DrawCopyableCommand(manual);
			}
		}

		/// <summary>
		/// Per-dependency manual steps for deps that have no package-manager install on this platform
		/// (e.g. Windows VS Build Tools, macOS Xcode CLT): show a copy-pasteable command and/or a
		/// download link the user can open in a browser.
		/// </summary>
		private void DrawManualOnlyDependencies()
		{
			var manualOnly = _result.ManualOnlyMissing.ToList();
			if (manualOnly.Count == 0)
				return;

			ImGui.Spacing();
			ImGuiSafe.TextColoredSafe(Colors.Header, "Required downloads / steps");
			ImGui.Separator();
			ImGui.Spacing();

			foreach (var status in manualOnly)
			{
				var dep = status.Dependency;
				ImGuiSafe.TextSafe("- " + dep.FriendlyName);
				ImGui.Indent();

				if (!string.IsNullOrEmpty(dep.ManualInstruction))
				{
					// If it looks like a runnable command, render it copyable; else as wrapped text.
					if (LooksLikeCommand(dep.ManualInstruction))
						DrawCopyableCommand(dep.ManualInstruction);
					else
						ImGuiSafe.TextWrappedSafe(dep.ManualInstruction);
				}

				if (!string.IsNullOrEmpty(dep.DownloadUrl))
					DrawCopyableLink(dep.DownloadUrl);

				ImGui.Unindent();
				ImGui.Spacing();
			}
		}

		private static bool LooksLikeCommand(string s) =>
			s.StartsWith("xcode-select", StringComparison.Ordinal) ||
			s.StartsWith("winget", StringComparison.Ordinal) ||
			s.StartsWith("brew", StringComparison.Ordinal) ||
			s.StartsWith("sudo", StringComparison.Ordinal);

		/// <summary>Draws a download URL the user can copy or open in their default browser.</summary>
		private void DrawCopyableLink(string url)
		{
			if (string.IsNullOrEmpty(url))
				return;

			ImGui.PushID(url);
			var editable = url;
			ImGui.SetNextItemWidth(420);
			Gui.InputText("##url", ref editable, (uint)url.Length + 1, ImGuiInputTextFlags.ReadOnly);
			ImGui.SameLine();
			if (Gui.SmallButton("Copy"))
				Voltage.Clipboard.SetContents(url);
			ImGui.SameLine();
			if (Gui.SmallButton("Open"))
				OpenUrl(url);
			ImGui.PopID();
		}

		private static void OpenUrl(string url)
		{
			try
			{
				System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
				{
					FileName = url,
					UseShellExecute = true // lets the OS open it in the default browser
				});
			}
			catch (Exception ex)
			{
				Voltage.Debug.Warn($"Could not open URL '{url}': {ex.Message}");
			}
		}

		private void DrawCopyableCommand(string command)
		{
			if (string.IsNullOrEmpty(command))
				return;

			ImGui.PushStyleColor(ImGuiCol.ChildBg, new Num.Vector4(0.10f, 0.10f, 0.12f, 1.0f));
			ImGui.PushStyleColor(ImGuiCol.Text, new Num.Vector4(0.85f, 0.95f, 0.85f, 1.0f));

			// A read-only selectable so the user can drag-select; plus an explicit Copy button.
			ImGui.PushID(command);
			Gui.InputText("##cmd", ref command, (uint)command.Length + 1, ImGuiInputTextFlags.ReadOnly);
			ImGui.SameLine();
			if (Gui.SmallButton("Copy"))
				Voltage.Clipboard.SetContents(command);
			ImGui.PopID();

			ImGui.PopStyleColor(2);
		}

		private void DrawActionButtons()
		{
			const float buttonHeight = 30f;

			bool busy = _isInstalling;
			if (busy)
				ImGui.BeginDisabled();

			// Install automatically (only when actually supported)
			if (_result.CanAttemptAutoInstall)
			{
				ImGui.PushStyleColor(ImGuiCol.Button, new Num.Vector4(0.15f, 0.5f, 0.2f, 1.0f));
				ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Num.Vector4(0.2f, 0.65f, 0.3f, 1.0f));
				if (Gui.Button("Install automatically", new Num.Vector2(180, buttonHeight)))
					RunAutoInstall();
				ImGui.PopStyleColor(2);

				if (ImGui.IsItemHovered())
				{
					var via = _result.PackageManager == PackageManagerKind.Winget ? "winget" : "pkexec";
					ImGuiSafe.SetTooltipSafe($"Runs the install command above via {via}.\nThe system will ask you to confirm / authorize.");
				}

				ImGui.SameLine();
			}

			if (Gui.Button("Recheck", new Num.Vector2(120, buttonHeight)))
			{
				_result = NativeDependencyChecker.ReCheck(_dependencySet);
				if (_result.AllPresent)
				{
					_statusMessage = "All dependencies are now present.";
					_statusColor = Colors.Good;
				}
			}

			ImGui.SameLine();

			// Proceed button. When everything is present it's the blue "Continue"; when deps are still missing
			// but proceeding is allowed (advisory build flow) it's an amber "Build anyway" — the real publish
			// is the authority, so a detection miss must not trap the user. Hidden when no build is pending.
			bool canProceed = _onProceed != null && (_result.AllPresent || _allowProceedWhenMissing);
			if (canProceed)
			{
				bool present = _result.AllPresent;
				var label = present ? "Continue" : "Build anyway";
				var color = present
					? new Num.Vector4(0.15f, 0.45f, 0.7f, 1.0f)   // blue
					: new Num.Vector4(0.7f, 0.5f, 0.15f, 1.0f);   // amber

				ImGui.PushStyleColor(ImGuiCol.Button, color);
				bool clicked = Gui.Button(label, new Num.Vector2(120, buttonHeight));
				ImGui.PopStyleColor();

				if (!present && ImGui.IsItemHovered())
					ImGuiSafe.SetTooltipSafe(
						"Some tools weren't detected, but detection isn't perfect. If you installed them in a\n" +
						"non-standard way the build can still succeed; the publish will report a specific error\n" +
						"if anything is genuinely missing.");

				if (clicked)
				{
					if (busy) ImGui.EndDisabled();
					Close();
					_onProceed.Invoke();
					return;
				}
				ImGui.SameLine();
			}

			if (Gui.Button("Cancel", new Num.Vector2(120, buttonHeight)))
			{
				if (busy) ImGui.EndDisabled();
				Close();
				_onCancel?.Invoke();
				return;
			}

			if (busy)
			{
				ImGui.EndDisabled();
				ImGui.SameLine();
				ImGui.TextColored(Colors.Info, "Installing...");
			}
		}

		private void RunAutoInstall()
		{
			// pkexec blocks on its auth dialog; run it off the UI thread so the editor keeps rendering.
			_isInstalling = true;
			_statusMessage = "Requesting authorization and installing...";
			_statusColor = Colors.Info;

			var capturedResult = _result;
			var capturedSet = _dependencySet;

			System.Threading.Tasks.Task.Run(() =>
			{
				var outcome = NativeDependencyChecker.TryAutoInstall(capturedResult);
				var rechecked = outcome.Succeeded
					? NativeDependencyChecker.ReCheck(capturedSet)
					: capturedResult;

				// Marshal results back; ImGui is single-threaded, so just set fields the Draw loop reads.
				_result = rechecked;
				_isInstalling = false;

				if (outcome.Succeeded && rechecked.AllPresent)
				{
					_statusMessage = "All dependencies installed successfully.";
					_statusColor = Colors.Good;
				}
				else if (outcome.Succeeded)
				{
					_statusMessage = "Install ran, but some dependencies are still missing. See the list above.";
					_statusColor = Colors.Warn;
				}
				else
				{
					_statusMessage = outcome.Message + " You can still install manually using the command above.";
					_statusColor = Colors.Bad;
				}
			});
		}

		private void Close()
		{
			_open = false;
			_requestOpen = false;
			ImGui.CloseCurrentPopup();
		}

		private static string DescribePackageManager(PackageManagerKind pm) => pm switch
		{
			PackageManagerKind.Pacman => "pacman",
			PackageManagerKind.Apt => "apt",
			PackageManagerKind.Dnf => "dnf",
			PackageManagerKind.Zypper => "zypper",
			_ => "unknown package manager"
		};
	}
}
