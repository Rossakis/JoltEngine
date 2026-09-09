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
using Voltage.Gateway;
using static Voltage.Editor.Gateway.GatewayValues;

namespace Voltage.Editor.Gateway.Commands;

/// <summary>Assets, game builds, lifecycle events and a deliberate crash for testing recovery.</summary>
internal static class WorkflowCommands
{
	private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(30);
	private static readonly TimeSpan GameGatewayTimeout = TimeSpan.FromSeconds(30);
	private static bool _building;

	public static void Register(GatewayCommandTable table)
	{
		table.Add("asset.list", "Indexed project assets.", (args, _) =>
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
		}, P.Str("filter", "Substring of the absolute path"), P.Str("kind", "Asset kind from asset.kinds, such as Texture, Prefab, Scene, Script, Effect, Tiled, Audio, Timeline or Tileset")).ReadOnly();

		table.Add("asset.get", "One asset by GUID or path.", (args, _) =>
		{
			var db = RequireAssets();
			var item = GatewayValues.ResolveAsset(args.Require("asset"));
			return Describe(db, item);
		}, AssetParam).ReadOnly();

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
				.ToList()).ReadOnly();

		table.Add("asset.drop", "Drop an asset into the scene as the asset browser would: a prefab instantiates, a texture or Aseprite file becomes a sprite entity.", (args, ctx) =>
		{
			var db = RequireAssets();
			var item = GatewayValues.ResolveAsset(args.Require("asset"));
			var drop = item.Descriptor.DropFactory ?? throw new GatewayException($"{item.Descriptor.Kind} assets cannot be dropped into a scene");
			if (Core.Scene == null)
				throw new GatewayException("no scene loaded");

			var pane = ctx.ImGui().SceneGraphWindow?.EntityPane;
			var before = pane != null ? new HashSet<Entity>(pane.SelectedEntities) : null;
			drop(db.GetReference(item.AbsolutePath), new Vector2(args.Float("x"), args.Float("y")));

			var created = pane?.SelectedEntities.Where(e => !before.Contains(e)).Select(e => new { id = e.Id, guid = e.PersistentId, name = e.Name }).ToList();
			return new { dropped = item.FileName, selected = created };
		}, AssetParam, P.Float("x", "World position", 0f), P.Float("y", "World position", 0f));

		table.Add("build.platforms", "Game build targets and whether this machine can build them.", (_, _) =>
			BuildPlatform.All.Select(p => new { name = p.DisplayName, rid = p.RuntimeIdentifier, available = p.IsAvailable, reason = p.UnavailableReason }).ToList()).ReadOnly();

		table.Add("build.game", "Publish the game; answers when the build finishes.", (args, _) =>
		{
			var project = ProjectManager.Instance.CurrentProject ?? throw new GatewayException("no project loaded");
			if (_building)
				throw new GatewayException("a build is already running");

			var platform = ResolvePlatform(args.String("platform"));
			if (!platform.IsAvailable)
				throw new GatewayException($"{platform.DisplayName} cannot be built here: {platform.UnavailableReason}");

			var debug = args.Bool("debug");
			var aot = args.Bool("aot", true);
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
					var success = await GameBuilder.BuildGameAsync(project, platform, args.Bool("compileAssets", true), debug, args.Bool("linuxContainer"), aot, CancellationToken.None);
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
						aot,
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
		}, PlatformParam, DebugParam, P.Bool("compileAssets", "Compile content before publishing", true), P.Bool("linuxContainer", "Build Linux targets inside a container", false), P.Bool("aot", "Native AOT publish; false is a plain self-contained build that needs no C++ toolchain", true)).Unsafe();

		table.Add("build.run", "Launch the last built game executable, detached from the editor. With gateway=true the game starts its own gateway on a free port and the answer carries its gateway.json path once it is listening.", (args, ctx) =>
		{
			var project = ProjectManager.Instance.CurrentProject ?? throw new GatewayException("no project loaded");
			var platform = ResolvePlatform(args.String("platform"));

			var exe = GameBuilder.FindGameExecutable(project, platform, args.Bool("debug"));
			if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
				throw new GatewayException("no built executable found; run build.game first");

			var startInfo = new System.Diagnostics.ProcessStartInfo(exe)
			{
				WorkingDirectory = Path.GetDirectoryName(exe) ?? ".",
				UseShellExecute = true
			};
			var gateway = args.Bool("gateway");
			var infoPath = GatewayStorage.RuntimeInfoPath;
			if (gateway)
				foreach (var arg in new[] { "--gateway", "--gateway-port", "0", "--gateway-info", infoPath })
					startInfo.ArgumentList.Add(arg);

			var process = System.Diagnostics.Process.Start(startInfo) ?? throw new GatewayException($"could not start {exe}");
			if (!gateway)
				return new { pid = process.Id, executable = exe };
			return WaitForGameGateway(process, exe, infoPath);
		}, PlatformParam, DebugParam, P.Bool("gateway", "Start the game's own gateway and wait for it", false)).Unsafe();

		table.Add("debug.crash", "Kill the editor with an unhandled exception, to test crash logging and relaunch.", (args, _) =>
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
		}, P.Bool("confirm", "Must be true", required: true)).Destructive().Unsafe();
	}

	private static readonly GatewayParam AssetParam = P.Str("asset", "Asset GUID or path", required: true);
	private static readonly GatewayParam PlatformParam = P.Str("platform", "Display name or RID from build.platforms; the first available when omitted");
	private static readonly GatewayParam DebugParam = P.Bool("debug", "Debug build instead of release", false);

	/// <summary>Polls for the runtime gateway.json the launched game writes, matched by pid so a stale file cannot answer.</summary>
	private static Task<object> WaitForGameGateway(System.Diagnostics.Process process, string exe, string infoPath)
	{
		return GatewayTasks.WhenReady(() => process.HasExited || TryReadPort(out _), () =>
		{
			if (process.HasExited)
				throw new GatewayException($"the game exited during startup with code {process.ExitCode}");
			if (!TryReadPort(out var port))
				throw new GatewayException($"the game (pid {process.Id}) did not write {infoPath} within {GameGatewayTimeout.TotalSeconds:0}s");
			return new { pid = process.Id, executable = exe, gatewayInfo = infoPath, port };
		}, (float)GameGatewayTimeout.TotalSeconds);

		bool TryReadPort(out int port)
		{
			port = 0;
			try
			{
				if (!File.Exists(infoPath))
					return false;
				using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(infoPath));
				if (!doc.RootElement.TryGetProperty("pid", out var pid) || pid.GetInt32() != process.Id)
					return false;
				port = doc.RootElement.GetProperty("port").GetInt32();
				return true;
			}
			catch (Exception)
			{
				return false;
			}
		}
	}

	/// <summary>Display name or RID; the first buildable platform when none is given.</summary>
	private static BuildPlatform ResolvePlatform(string wanted)
	{
		if (string.IsNullOrEmpty(wanted))
			return BuildPlatform.Available.FirstOrDefault() ?? BuildPlatform.Default;
		return BuildPlatform.All.FirstOrDefault(p => p.DisplayName.Equals(wanted, StringComparison.OrdinalIgnoreCase) || p.RuntimeIdentifier.Equals(wanted, StringComparison.OrdinalIgnoreCase))
			?? throw new GatewayException($"unknown platform '{wanted}'; see build.platforms");
	}

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
