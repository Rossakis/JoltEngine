using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Voltage.Editor.Assets;
using Voltage.Editor.Builders;
using Voltage.Editor.ProjectFile;

namespace Voltage.Editor.Gateway.Commands;

/// <summary>Assets, game builds, lifecycle events and a deliberate crash for testing recovery.</summary>
internal static class WorkflowCommands
{
	private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(30);
	private static bool _building;

	public static void Register(GatewayCommandTable table)
	{
		table.Add("asset.list", "Indexed project assets. params: filter (substring of the path), kind (Texture|Prefab|Scene|Script|Effect|Tiled|Audio|Timeline|Tileset|...)", (args, _) =>
		{
			var db = RequireAssets();
			var filter = args.String("filter");
			var kind = args.String("kind");
			return db.Items
				.DistinctBy(i => i.AbsolutePath, StringComparer.OrdinalIgnoreCase)
				.Where(i => string.IsNullOrEmpty(filter) || i.AbsolutePath.Contains(filter, StringComparison.OrdinalIgnoreCase))
				.Where(i => string.IsNullOrEmpty(kind) || i.Descriptor.Kind.ToString().Equals(kind, StringComparison.OrdinalIgnoreCase))
				.Select(i => Describe(db, i))
				.ToList();
		});

		table.Add("asset.get", "One asset by GUID or path. params: asset", (args, _) =>
		{
			var db = RequireAssets();
			var item = GatewayValues.ResolveAsset(args.Require("asset"));
			return Describe(db, item);
		});

		table.Add("asset.refresh", "Re-index the project's asset folders.", (_, _) =>
		{
			var db = RequireAssets();
			db.Refresh();
			return new { indexed = db.Items.Count };
		});

		table.Add("asset.kinds", "Registered asset types, their extensions and whether they can be dropped into the scene.", (_, _) =>
			AssetTypeRegistry.AllDescriptors
				.Distinct()
				.Select(d => new { kind = d.Kind.ToString(), extensions = d.Extensions, droppable = d.DropFactory != null })
				.OrderBy(d => d.kind)
				.ToList());

		table.Add("asset.drop", "Drop an asset into the scene as the asset browser would: a prefab instantiates, a texture or Aseprite file becomes a sprite entity. params: asset (GUID or path), x=0, y=0", (args, ctx) =>
		{
			var db = RequireAssets();
			var item = GatewayValues.ResolveAsset(args.Require("asset"));
			var drop = item.Descriptor.DropFactory ?? throw new GatewayException($"{item.Descriptor.Kind} assets cannot be dropped into a scene");
			if (Core.Scene == null)
				throw new GatewayException("no scene loaded");

			var pane = ctx.ImGui.SceneGraphWindow?.EntityPane;
			var before = pane != null ? new HashSet<Entity>(pane.SelectedEntities) : null;
			drop(db.GetReference(item.AbsolutePath), new Vector2(args.Float("x"), args.Float("y")));

			var created = pane?.SelectedEntities.Where(e => !before.Contains(e)).Select(e => new { id = e.Id, guid = e.PersistentId, name = e.Name }).ToList();
			return new { dropped = item.FileName, selected = created };
		});

		table.Add("build.platforms", "Game build targets and whether this machine can build them.", (_, _) =>
			BuildPlatform.All.Select(p => new { name = p.DisplayName, rid = p.RuntimeIdentifier, available = p.IsAvailable, reason = p.UnavailableReason }).ToList());

		table.Add("build.game", "Publish the game; answers when the build finishes. params: platform (display name or RID, default first available), debug=false, compileAssets=true, linuxContainer=false", (args, _) =>
		{
			var project = ProjectManager.Instance.CurrentProject ?? throw new GatewayException("no project loaded");
			if (_building)
				throw new GatewayException("a build is already running");

			var platform = ResolvePlatform(args.String("platform"));
			if (!platform.IsAvailable)
				throw new GatewayException($"{platform.DisplayName} cannot be built here: {platform.UnavailableReason}");

			var debug = args.Bool("debug");
			var steps = new List<object>();
			var errors = new List<string>();
			var lockObj = new object();
			Action<string, bool> onStep = (step, ok) => { lock (lockObj) steps.Add(new { step, ok }); };
			Action<Debug.LogEntry> onLog = entry =>
			{
				if (entry.Type == Debug.LogType.Error)
					lock (lockObj) errors.Add(entry.Message);
			};
			GameBuilder.OnBuildStepCompleted += onStep;
			Debug.OnLogEntry += onLog;
			_building = true;

			var tcs = new TaskCompletionSource<object>();
			Task.Run(async () =>
			{
				try
				{
					var success = await GameBuilder.BuildGameAsync(project, platform, args.Bool("compileAssets", true), debug, args.Bool("linuxContainer"), CancellationToken.None);
					GameBuilder.OnBuildStepCompleted -= onStep;
					Debug.OnLogEntry -= onLog;

					string exe = null;
					try { exe = success ? GameBuilder.FindGameExecutable(project, platform, debug) : null; } catch (Exception) { }
					object[] stepsCopy;
					string[] errorsCopy;
					lock (lockObj)
					{
						stepsCopy = steps.ToArray();
						errorsCopy = errors.ToArray();
					}
					tcs.TrySetResult(new
					{
						success,
						platform = platform.DisplayName,
						output = GameBuilder.GetBuildOutputDirectory(project, platform, debug),
						executable = exe,
						steps = stepsCopy,
						errors = errorsCopy
					});
				}
				catch (Exception ex)
				{
					tcs.TrySetException(new GatewayException($"build failed: {ex.Message}"));
				}
				finally
				{
					GameBuilder.OnBuildStepCompleted -= onStep;
					Debug.OnLogEntry -= onLog;
					_building = false;
				}
			});

			return GatewayTasks.WithTimeout(tcs.Task, BuildTimeout, "build timed out");
		});

		table.Add("build.run", "Launch the last built game executable, detached from the editor. params: platform (default first available), debug=false", (args, _) =>
		{
			var project = ProjectManager.Instance.CurrentProject ?? throw new GatewayException("no project loaded");
			var platform = ResolvePlatform(args.String("platform"));

			var exe = GameBuilder.FindGameExecutable(project, platform, args.Bool("debug"))
				?? throw new GatewayException("no built executable found; run build.game first");
			var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe)
			{
				WorkingDirectory = Path.GetDirectoryName(exe) ?? ".",
				UseShellExecute = true
			}) ?? throw new GatewayException($"could not start {exe}");
			return new { pid = process.Id, executable = exe };
		});

		table.Add("events.subscribe", "Stream editor lifecycle events (scene, project, play mode, compile) to this connection as 'editor' events.", (_, ctx) =>
		{
			ctx.Client.EventsSubscribed = true;
			return new { subscribed = true };
		});

		table.Add("events.unsubscribe", "Stop streaming editor lifecycle events.", (_, ctx) =>
		{
			ctx.Client.EventsSubscribed = false;
			return new { subscribed = false };
		});

		table.Add("debug.crash", "Kill the editor with an unhandled exception, to test crash logging and relaunch. params: confirm=true", (args, _) =>
		{
			if (!args.Bool("confirm"))
				throw new GatewayException("pass confirm=true to crash the editor on purpose");

			// The frame loop catches main-thread exceptions, so a worker thread is what actually terminates the process.
			var thread = new Thread(() =>
			{
				Thread.Sleep(200);
				throw new InvalidOperationException("Deliberate crash requested through the gateway");
			}) { IsBackground = true, Name = "Gateway deliberate crash" };
			thread.Start();
			return new { crashing = true };
		});
	}

	/// <summary>Display name or RID; the first buildable platform when none is given.</summary>
	private static BuildPlatform ResolvePlatform(string wanted)
	{
		if (string.IsNullOrEmpty(wanted))
			return BuildPlatform.Available.FirstOrDefault() ?? BuildPlatform.Default;
		return BuildPlatform.All.FirstOrDefault(p => p.DisplayName.Equals(wanted, StringComparison.OrdinalIgnoreCase) || p.RuntimeIdentifier.Equals(wanted, StringComparison.OrdinalIgnoreCase))
			?? throw new GatewayException($"unknown platform '{wanted}'; see build.platforms");
	}

	private static AssetDatabase RequireAssets() =>
		AssetDatabase.Instance ?? throw new GatewayException("no project loaded; the asset database is empty");

	private static object Describe(AssetDatabase db, AssetItem item) => new
	{
		name = item.FileName,
		path = item.AbsolutePath,
		folder = item.FolderLabel,
		extension = item.Extension,
		kind = item.Descriptor.Kind.ToString(),
		droppable = item.Descriptor.DropFactory != null,
		guid = db.GetReference(item.AbsolutePath).Guid
	};
}
