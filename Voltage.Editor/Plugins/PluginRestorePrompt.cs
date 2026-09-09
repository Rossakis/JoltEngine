using System;
using System.Collections.Generic;
using System.Linq;
using ImGuiNET;
using Num = System.Numerics;

namespace Voltage.Editor.Plugins
{
	/// <summary>
	/// The "you are missing plugins this project needs" flow: what <c>npm install</c> does when a teammate
	/// pulls a branch that added a dependency.
	///
	/// <para>A project declares its plugins in the committed <c>plugins.json</c> and pins exactly what they
	/// resolved to in <c>plugins.lock.json</c>. Someone opening that project for the first time has neither the
	/// payloads nor any prompt to fetch them - the plugins simply come up Unavailable in a window they may
	/// never open, and every component those plugins provide shows as a missing component. This turns that into
	/// one dialog and one button.</para>
	///
	/// <para>Everything that failed to come up is listed, whatever the cause - a plugin nobody published, a
	/// local folder that has been moved or renamed, a source committed by someone else. Only a git or zip
	/// source can actually be downloaded, so that decides whether the Install button does anything, not
	/// whether the problem is worth telling you about. Silence is the one outcome this must never produce.</para>
	/// </summary>
	public static class PluginRestorePrompt
	{
		/// <summary>Why a plugin is unavailable, which is also what can be done about it.</summary>
		private enum Kind
		{
			/// <summary>The project names a git or zip source: this machine can go and get it.</summary>
			Fetchable,

			/// <summary>The project names it but nowhere to get it - somebody has it locally and never shared it.</summary>
			Unpublished,

			/// <summary>This machine points it at a folder of its own, and that folder is not there.</summary>
			BrokenLocal,

			/// <summary>Declared as a folder inside the repository, which this checkout does not have.</summary>
			MissingInRepo,
		}

		private sealed class Pending
		{
			public string Id;
			public ProjectPluginEntry Entry;
			public string DisplayName;
			public string Reason;
			public Kind Kind;

			public bool Unfetchable => Kind != Kind.Fetchable;
		}

		private static readonly List<Pending> _pending = new();
		private static readonly Queue<Pending> _queue = new();

		private static bool _open;

		// OpenPopup is called once per raise, not every frame: calling it repeatedly on an already-open popup
		// re-opens it, which resets the position the user just dragged it to.
		private static bool _openRequested;

		private static bool _installing;
		private static string _lastSignature;

		/// <summary>Dismissed for this project until it is reopened or the set of missing plugins changes.</summary>
		private static string _dismissedSignature;

		private static string _status;

		/// <summary>How many plugins the project declares that this machine cannot use. Zero when all is well.</summary>
		public static int MissingCount => _pending.Count;

		internal static bool IsInstalling => _installing;

		internal static string Status => _status;

		/// <summary>The prompt's own reading of a plugin: fetchable, unpublished, brokenLocal, missingInRepo, or null when it is fine.</summary>
		internal static string Describe(PluginInstance plugin)
		{
			if (!NeedsAttention(plugin))
				return null;
			return Classify(plugin) switch
			{
				Kind.Fetchable => "fetchable",
				Kind.Unpublished => "unpublished",
				Kind.BrokenLocal => "brokenLocal",
				_ => "missingInRepo"
			};
		}

		private static string Describe(Pending pending) => pending.Kind switch
		{
			Kind.Fetchable => "fetchable",
			Kind.Unpublished => "unpublished",
			Kind.BrokenLocal => "brokenLocal",
			_ => "missingInRepo"
		};

		/// <summary>Queues the fetchable plugins (one id, or all) exactly as the Install button does; returns how many were queued.</summary>
		internal static int Install(string id = null)
		{
			_queue.Clear();
			foreach (var pending in _pending)
			{
				if (pending.Unfetchable)
					continue;
				if (id != null && !string.Equals(pending.Id, id, StringComparison.OrdinalIgnoreCase))
					continue;
				_queue.Enqueue(pending);
			}

			_installing = _queue.Count > 0;
			_status = _installing ? "Starting..." : null;
			return _queue.Count;
		}

		/// <summary>
		/// Recomputes what is missing and raises the prompt when that set changes. Cheap enough to call every
		/// frame: it reads already-resolved state and touches no disk.
		/// </summary>
		public static void Update(IReadOnlyList<PluginInstance> plugins)
		{
			_pending.Clear();

			if (plugins != null)
			{
				foreach (var plugin in plugins)
				{
					if (!NeedsAttention(plugin))
						continue;

					_pending.Add(new Pending
					{
						Id = plugin.Id,
						Entry = plugin.Entry,
						DisplayName = plugin.DisplayName ?? plugin.Id,
						Reason = plugin.Error,
						Kind = Classify(plugin),
					});
				}
			}

			var signature = string.Join("|", _pending.Select(p => p.Entry?.Id).OrderBy(id => id, StringComparer.OrdinalIgnoreCase));

			// Only ask again when the answer would be different: reopening the dialog every frame after it was
			// dismissed, or every time the list is recomputed, would make it impossible to work around.
			if (!string.Equals(signature, _lastSignature, StringComparison.Ordinal))
			{
				_lastSignature = signature;

				if (_pending.Count > 0 && !string.Equals(signature, _dismissedSignature, StringComparison.Ordinal))
				{
					if (EditorRunMode.NoPrompts)
					{
						_dismissedSignature = signature;
						Gateway.EditorGatewayDispatcher.SuppressPrompt("Plugins Needed",
							$"{string.Join(", ", _pending.Select(p => $"{p.DisplayName} ({Describe(p)})"))}; see plugin.list and plugin.restore");
					}
					else
					{
						_open = true;
						_openRequested = true;
					}
				}
			}

			if (_pending.Count == 0 && !_installing)
				_open = false;
		}

		/// <summary>Raises the prompt again on demand, for the "Restore plugins" menu action.</summary>
		public static void Show()
		{
			_dismissedSignature = null;
			_open = _pending.Count > 0;
			_openRequested = _open;
		}

		/// <summary>
		/// Draws the prompt and drives the install queue. Call every frame from somewhere that runs whether or
		/// not the Plugin Manager is open - a teammate who never opens that window is exactly who this is for.
		/// </summary>
		public static void Draw()
		{
			if (_installing)
				PumpQueue();

			if (!_open)
				return;

			if (_openRequested)
			{
				ImGui.OpenPopup("Plugins Needed###PluginRestorePrompt");
				_openRequested = false;
			}

			var centre = ImGui.GetMainViewport().GetCenter();
			ImGui.SetNextWindowPos(centre, ImGuiCond.Appearing, new Num.Vector2(0.5f, 0.5f));
			ImGui.SetNextWindowSize(new Num.Vector2(640, 0), ImGuiCond.Appearing);

			var open = true;
			if (!Gui.BeginPopupModal("Plugins Needed###PluginRestorePrompt", ref open,
				    ImGuiWindowFlags.AlwaysAutoResize))
			{
				return;
			}

			ImGui.TextWrapped(_pending.Count == 1
				? "A plugin this project uses is not available:"
				: $"{_pending.Count} plugins this project uses are not available:");

			ImGui.Spacing();
			ImGui.Separator();

			foreach (var pending in _pending)
			{
				ImGui.PushID(pending.Id ?? pending.DisplayName ?? "?");

				ImGui.TextUnformatted(pending.DisplayName ?? "(unknown)");
				ImGui.SameLine();
				ImGui.TextColored(new Num.Vector4(0.6f, 0.6f, 0.6f, 1f),
					pending.Entry?.Source?.Describe() ?? "-");

				ImGui.Indent(12f);

				if (!string.IsNullOrWhiteSpace(pending.Reason))
					ImGui.TextColored(new Num.Vector4(0.75f, 0.7f, 0.4f, 1f), Shorten(pending.Reason, 140));

				switch (pending.Kind)
				{
					case Kind.BrokenLocal:
						ImGui.TextWrapped(
							"This is your own folder for this plugin, and it is not there any more - moved, " +
							"renamed, or on a drive that is not mounted. Nothing to download: point it at the " +
							"folder again, or drop it and use whatever the project declares.");
						break;

					case Kind.Unpublished:
						ImGui.TextWrapped(
							"The project records that it uses this plugin, but names no source to get it from. " +
							"Whoever added it has to publish it, or vendor it into the repository - or, if you " +
							"already have a copy of it on disk, point this at it.");
						break;

					case Kind.MissingInRepo:
						ImGui.TextWrapped(
							"The project expects this plugin as a folder inside the repository, and it is not " +
							"there. Nothing to download - the checkout is missing files. Pull, or check whether " +
							"the folder was ever committed.");
						break;
				}

				if (pending.Kind != Kind.Fetchable)
					DrawRowActions(pending);

				ImGui.Unindent(12f);
				ImGui.PopID();
			}

			ImGui.Separator();

			if (!string.IsNullOrWhiteSpace(_status))
			{
				ImGui.TextColored(new Num.Vector4(0.5f, 0.8f, 1f, 1f), _status);
				ImGui.Spacing();
			}

			var fetchable = _pending.Count(p => !p.Unfetchable);

			ImGui.BeginDisabled(_installing || fetchable == 0);

			if (Gui.Button(fetchable > 0 && fetchable < _pending.Count ? $"Install {fetchable}" : "Install All",
				    new Num.Vector2(140, 0)))
			{
				StartInstall();
			}

			ImGui.EndDisabled();

			if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && fetchable == 0)
			{
				ImGui.SetTooltip(
					"Nothing here is a download. Each plugin above says what it actually needs - a folder\n" +
					"pointed at again, or somebody to publish it.");
			}

			ImGui.SameLine();

			if (Gui.Button(_installing ? "Hide" : "Not now", new Num.Vector2(140, 0)))
			{
				// Remembered by which plugins were missing, so it stays quiet until that actually changes.
				_dismissedSignature = _lastSignature;
				_open = false;
				ImGui.CloseCurrentPopup();
			}

			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip(_installing
					? "Closes this dialog. The installs keep running - progress is in the Plugin Manager."
					: "The plugins stay unavailable, and anything they provide keeps showing as missing.\n" +
					  "Plugins > Restore Plugins asks again.");
			}

			Gui.EndPopup();

			if (!open)
			{
				_dismissedSignature = _lastSignature;
				_open = false;
			}
		}

		/// <summary>
		/// What can be done about a plugin that is not a download: point it at a copy on this disk, drop a
		/// local folder that has gone, or - when a registry happens to list this exact id - fetch it after all.
		/// </summary>
		private static void DrawRowActions(Pending pending)
		{
			if (Gui.SmallButton("Browse"))
				Locate(pending);

			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip(
					"Point this plugin at a folder on this machine - a checkout of it you already have, or the\n" +
					"folder it moved to. The folder has to hold a plugin.json with this plugin's id.\n\n" +
					"Kept in plugins.local.json, which is not committed: it is your path, not the team's.");
			}

			if (pending.Kind == Kind.BrokenLocal)
			{
				ImGui.SameLine();

				if (Gui.SmallButton("Forget local folder"))
					_status = PluginManager.Instance.ForgetLocalOverride(pending.Id);

				if (ImGui.IsItemHovered())
				{
					ImGui.SetTooltip(
						"Removes this machine's local folder for the plugin. The project's own source is used\n" +
						"instead - or, if it has none, it is reported as never published.");
				}
			}

			// Only when a registry genuinely lists this id. There is no searching the internet for a plugin by
			// name: whatever a search turned up would be a repository claiming an id, which is precisely how you
			// would install somebody else's code under a name you trust.
			var listing = PluginRegistryIndex.FindById(pending.Id);
			if (listing == null)
				return;

			ImGui.SameLine();

			if (Gui.SmallButton($"Install {listing.VersionLabel} from registry"))
			{
				var started = PluginInstaller.Start(
					new ProjectPluginEntry { Id = pending.Id, Source = listing.ToSourceSpec() },
					pending.DisplayName, isUpdate: true);

				_status = started == null
					? "Another install is already running."
					: $"Fetching {pending.DisplayName} from {listing.RegistryName ?? "the registry"}...";
			}

			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip(
					$"{listing.RegistryName ?? "A registry"} lists this plugin id. Installing points the project\n" +
					"at that source, so everyone else restores the same thing.");
			}
		}

		/// <summary>
		/// Asks for the folder the plugin moved to. Starts at the path that no longer works, so the browser
		/// opens as close to it as still exists rather than at some default.
		/// </summary>
		private static void Locate(Pending pending)
		{
			var start = pending.Entry?.Source?.Path;

			if (!Voltage.Editor.FilePickers.NativeFileDialogs.TryPickFolder(
				    $"Folder for {pending.DisplayName}", start, out var folder)
			    || string.IsNullOrWhiteSpace(folder))
			{
				return;
			}

			_status = PluginManager.Instance.RepointLocalOverride(pending.Id, folder);
		}

		private static void StartInstall() => Install();

		/// <summary>
		/// Feeds the queue into the installer one at a time - it takes a single job at a time by design, since
		/// each one rewrites the project's plugin files and loads assemblies.
		/// </summary>
		private static void PumpQueue()
		{
			if (PluginInstaller.IsBusy)
				return;

			if (_queue.Count == 0)
			{
				_installing = false;
				_status = null;
				return;
			}

			var next = _queue.Dequeue();

			// isUpdate: the entry is already in plugins.json - this is fetching what it names, not adding it.
			var job = PluginInstaller.Start(next.Entry, next.DisplayName, isUpdate: true);

			_status = job == null
				? $"Waiting to fetch {next.DisplayName}..."
				: $"Fetching {next.DisplayName} ({_queue.Count} left)...";

			// Start returned nothing because something else is installing; put it back and retry next frame.
			if (job == null)
				_queue.Enqueue(next);
		}

		/// <summary>
		/// A plugin the project declares, that this machine cannot use, and that can be fetched from where the
		/// project says it lives.
		/// </summary>
		/// <summary>
		/// Every plugin the project declares that did not come up usable, whatever the reason.
		///
		/// <para>Deliberately not limited to the ones that can be fetched. Filtering by "can I fix this with a
		/// download" is what made a broken local folder, or a path committed by a teammate, fail in total
		/// silence - the plugin was simply absent and nothing said why. What can be downloaded decides whether
		/// the Install button does anything, not whether the problem is worth mentioning.</para>
		/// </summary>
		private static bool NeedsAttention(PluginInstance plugin)
		{
			// A plugin this machine deliberately points elsewhere and which loaded fine is not a problem; one
			// whose folder has gone missing very much is, and falls through to the state check below.
			if (plugin == null || plugin.Entry == null)
				return false;

			return plugin.State == PluginState.Unavailable;
		}

		/// <summary>
		/// What kind of problem this is. The distinction matters because the three have nothing in common:
		/// one is a download, one needs somebody else to publish something, and one is a folder on this
		/// machine that moved. Telling a user to "ask whoever added it to publish it" when they renamed their
		/// own checkout five minutes ago is worse than saying nothing.
		/// </summary>
		private static Kind Classify(PluginInstance plugin)
		{
			if (plugin.IsLocalOverride && !string.IsNullOrWhiteSpace(plugin.Entry?.Source?.Path))
				return Kind.BrokenLocal;

			var source = plugin.Entry?.Source;

			if (source != null && (!string.IsNullOrWhiteSpace(source.Git) || !string.IsNullOrWhiteSpace(source.Zip)))
				return Kind.Fetchable;

			// A path nobody overrode is one the project itself declares, which only ever means a folder that
			// travels with the repository. Missing means the checkout is short of files, not that anyone needs
			// to publish anything.
			if (!string.IsNullOrWhiteSpace(source?.Path))
				return Kind.MissingInRepo;

			return Kind.Unpublished;
		}

		private static string Shorten(string text, int max)
		{
			if (string.IsNullOrEmpty(text))
				return string.Empty;

			var oneLine = text.Replace("\r", " ").Replace("\n", " ").Trim();
			return oneLine.Length <= max ? oneLine : oneLine.Substring(0, max - 3) + "...";
		}
	}
}
