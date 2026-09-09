using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ImGuiNET;
using Voltage.Editor.Gateway.Commands;
using Voltage.Editor.ImGuiCore;
using Voltage.Editor.Persistence;
using Voltage.Editor.ProjectFile;
using Voltage.Editor.SceneFile;
using Voltage.Editor.Scripting;
using Voltage.Editor.Undo.Core;
using Voltage.Gateway;

namespace Voltage.Editor.Gateway;

/// <summary>The engine dispatcher plus the editor's commands and lifecycle events. Registered after ImGuiManager so it runs before the frame's layout.</summary>
public sealed class EditorGatewayDispatcher : GatewayDispatcher
{
	private ScriptManager _watchedScripts;
	private HashSet<uint> _entityIds = new();
	private List<uint> _selection = new();
	private bool _dirty;
	private bool _resync = true;
	private int _undoDepth;
	private Scene _snapshotScene;
	private HashSet<string> _modals = new(StringComparer.Ordinal);

	/// <summary>The editor's dispatcher, for UI code that reports through events (null before the editor is built).</summary>
	public static EditorGatewayDispatcher Current { get; private set; }

	/// <summary>Prompts kept closed by --no-prompts, since they fire before any client can subscribe.</summary>
	public static List<object> SuppressedPrompts { get; } = new();

	public ImGuiManager ImGuiManager { get; }

	public override string ScreenshotDirectory => Path.Combine(EditorStorage.CacheRoot, "Screenshots");

	public EditorGatewayDispatcher(GatewayOptions options, ImGuiManager imGui) : base(options)
	{
		ImGuiManager = imGui;
		Current = this;
		Input.TextSink = c => ImGui.GetIO().AddInputCharacter(c);

		EditorCommands.Register(Commands);
		ProjectSceneCommands.Register(Commands);
		EntityCommands.Register(Commands);
		PlayScriptCommands.Register(Commands);
		InputCommands.Register(Commands);
		WorkflowCommands.Register(Commands);
		SceneComponentCommands.Register(Commands);
		DataCommands.Register(Commands);
		ViewCommands.Register(Commands);
		UiCommands.Register(Commands);
		PrefabCommands.Register(Commands);
		TilemapCommands.Register(Commands);
		TimelineCommands.Register(Commands);
		AudioCommands.Register(Commands);
		AssetCommands.Register(Commands);
		EditorPromptCommands.Register(Commands);
	}

	/// <summary>A startup prompt that --no-prompts kept closed: warns in the log and tells subscribed agents what they would have been asked.</summary>
	public static void SuppressPrompt(string name, string detail)
	{
		Debug.Warn($"[Prompt suppressed] {name}: {detail}");
		SuppressedPrompts.Add(new { name, detail, time = DateTime.UtcNow });
		Current?.Emit("prompt.suppressed", new { name, detail });
	}

	protected override void OnStarted()
	{
		Core.OnSwitchEditMode += OnSwitchEditMode;
		Core.OnSwitchPauseMode += OnSwitchPauseMode;
		Core.OnResetScene += OnResetScene;
		SceneManager.Instance.OnSceneLoaded += OnSceneLoaded;
		SceneManager.Instance.OnSceneSaved += OnSceneSaved;
		SceneManager.Instance.OnSceneCreated += OnSceneCreated;
		ProjectManager.Instance.OnProjectLoaded += OnProjectLoaded;
		ProjectManager.Instance.OnProjectUnloaded += OnProjectUnloaded;
	}

	protected override void OnStopping()
	{
		Core.OnSwitchEditMode -= OnSwitchEditMode;
		Core.OnSwitchPauseMode -= OnSwitchPauseMode;
		Core.OnResetScene -= OnResetScene;
		SceneManager.Instance.OnSceneLoaded -= OnSceneLoaded;
		SceneManager.Instance.OnSceneSaved -= OnSceneSaved;
		SceneManager.Instance.OnSceneCreated -= OnSceneCreated;
		ProjectManager.Instance.OnProjectLoaded -= OnProjectLoaded;
		ProjectManager.Instance.OnProjectUnloaded -= OnProjectUnloaded;
		WatchScripts(null);
		if (Current == this)
			Current = null;
	}

	/// <summary>The script manager is rebuilt per project, so the compile hook follows the current instance.</summary>
	private void WatchScripts(ScriptManager current)
	{
		if (ReferenceEquals(_watchedScripts, current))
			return;

		if (_watchedScripts != null)
			_watchedScripts.OnCompilationComplete -= OnCompilationComplete;
		_watchedScripts = current;
		if (_watchedScripts != null)
			_watchedScripts.OnCompilationComplete += OnCompilationComplete;
	}

	private void OnSwitchEditMode(bool editMode) => Emit(editMode ? "play.stopped" : "play.started");
	private void OnSwitchPauseMode(bool paused) => Emit(paused ? "play.paused" : "play.resumed");
	private void OnResetScene() => Emit("scene.reset");
	private void OnSceneLoaded(string path) => Emit("scene.loaded", new { path });
	private void OnSceneSaved(string path) => Emit("scene.saved", new { path });
	private void OnSceneCreated(string name) => Emit("scene.created", new { name });
	private void OnProjectLoaded(IGameProject project)
	{
		SuppressedPrompts.Clear();
		Emit("project.loaded", new { name = project?.ProjectName, path = project?.ProjectPath });
	}
	private void OnProjectUnloaded() => Emit("project.unloaded");
	private void OnCompilationComplete(CompilationResult result, bool _) => Emit("scripts.compiled", new { success = result.Success, errors = result.Errors });

	public override void Update()
	{
		if (Server == null)
			return;

		WatchScripts(ImGuiManager.ScriptManager);
		base.Update();
		UiCommands.Tick();
		EmitStateChanges();
		EmitModalChanges();
	}

	/// <summary>Modals swallow every other click, so an agent needs to hear when one opens or closes.</summary>
	private void EmitModalChanges()
	{
		var open = new HashSet<string>(StringComparer.Ordinal);
		foreach (var window in UiRegistry.Windows)
			if (window.Kind == "modal")
				open.Add(window.Display);
		if (open.SetEquals(_modals))
			return;

		foreach (var name in open)
			if (!_modals.Contains(name))
				Emit("ui.modal", new { name, open = true });
		foreach (var name in _modals)
			if (!open.Contains(name))
				Emit("ui.modal", new { name, open = false });
		_modals = open;
	}

	/// <summary>Selection, dirty flag, undo depth and entity membership have no events of their own, so they are diffed once per frame.</summary>
	private void EmitStateChanges()
	{
		// Nobody is listening: skip the diff and resnapshot silently when the next client connects.
		if (Server.ClientCount == 0)
		{
			_resync = true;
			return;
		}

		if (_resync)
		{
			_resync = false;
			_dirty = EditorChangeTracker.IsDirty;
			_undoDepth = EditorChangeTracker.UndoActions.Count;
			_selection = ImGuiManager.SceneGraphWindow?.EntityPane?.SelectedEntities?.Select(e => e.Id).ToList() ?? new List<uint>();
			_snapshotScene = Core.Scene;
			_entityIds = Snapshot(Core.Scene, out _);
			return;
		}

		var dirty = EditorChangeTracker.IsDirty;
		if (dirty != _dirty)
		{
			_dirty = dirty;
			Emit("scene.dirty", new { dirty });
		}

		var undoDepth = EditorChangeTracker.UndoActions.Count;
		if (undoDepth != _undoDepth)
		{
			_undoDepth = undoDepth;
			Emit("undo.changed", new { undo = undoDepth, redo = EditorChangeTracker.RedoActions.Count });
		}

		var selected = ImGuiManager.SceneGraphWindow?.EntityPane?.SelectedEntities;
		var selection = selected == null ? new List<uint>() : selected.Select(e => e.Id).ToList();
		if (!selection.SequenceEqual(_selection))
		{
			_selection = selection;
			Emit("selection.changed", new { entities = selected?.Select(e => new { id = e.Id, guid = e.PersistentId, name = e.Name }).ToList() });
		}

		var scene = Core.Scene;
		if (!ReferenceEquals(scene, _snapshotScene))
		{
			// A new scene is a wholesale swap, not a stream of add and remove events.
			_snapshotScene = scene;
			_entityIds = Snapshot(scene, out _);
			return;
		}

		var current = Snapshot(scene, out var byId);
		if (current.SetEquals(_entityIds))
			return;

		var added = current.Where(id => !_entityIds.Contains(id)).ToList();
		var removed = _entityIds.Where(id => !current.Contains(id)).ToList();
		if (added.Count + removed.Count > 32)
			Emit("entities.changed", new { added = added.Count, removed = removed.Count });
		else
		{
			foreach (var id in added)
				Emit("entity.added", new { id, name = byId.TryGetValue(id, out var e) ? e.Name : null, guid = e?.PersistentId });
			foreach (var id in removed)
				Emit("entity.removed", new { id });
		}
		_entityIds = current;
	}

	private static HashSet<uint> Snapshot(Scene scene, out Dictionary<uint, Entity> byId)
	{
		byId = new Dictionary<uint, Entity>();
		var ids = new HashSet<uint>();
		if (scene == null)
			return ids;
		for (var i = 0; i < scene.Entities.Count; i++)
		{
			var entity = scene.Entities[i];
			ids.Add(entity.Id);
			byId[entity.Id] = entity;
		}
		return ids;
	}
}

/// <summary>Editor-only view of a request context.</summary>
internal static class GatewayContextExtensions
{
	public static ImGuiManager ImGui(this GatewayContext ctx) => ((EditorGatewayDispatcher)ctx.Dispatcher).ImGuiManager;
}
