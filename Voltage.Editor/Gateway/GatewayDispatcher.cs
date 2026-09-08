using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Voltage.Editor.Gateway.Commands;
using Voltage.Editor.ImGuiCore;
using Voltage.Editor.ProjectFile;
using Voltage.Editor.SceneFile;
using Voltage.Editor.Scripting;
using Voltage.Utils;

namespace Voltage.Editor.Gateway;

/// <summary>Drains gateway requests on the main thread each frame and answers them. Registered after ImGuiManager so it runs before the frame's layout.</summary>
public sealed class GatewayDispatcher : GlobalManager
{
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		IncludeFields = true,
		NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
		Converters = { new JsonStringEnumConverter() }
	};

	private readonly GatewayOptions _options;
	private readonly ImGuiManager _imGui;
	private readonly GatewayCommandTable _commands = new();
	private readonly List<PendingReply> _pending = new();
	private readonly List<WindowCapture> _captures = new();
	private GatewayServer _server;

	private sealed record PendingReply(GatewayClient Client, JsonElement Id, Task<object> Task);

	private sealed record WindowCapture(string Path, float Scale, Rectangle? Crop, TaskCompletionSource<object> Done);

	public GatewayCommandTable Commands => _commands;

	public GatewayServer Server => _server;

	internal InputSimulator Input { get; } = new();

	public GatewayDispatcher(GatewayOptions options, ImGuiManager imGui)
	{
		_options = options;
		_imGui = imGui;

		EditorCommands.Register(_commands);
		ProjectSceneCommands.Register(_commands);
		EntityCommands.Register(_commands);
		PlayScriptCommands.Register(_commands);
		InputCommands.Register(_commands);
		WorkflowCommands.Register(_commands);
		SceneComponentCommands.Register(_commands);
		DataCommands.Register(_commands);
		ViewCommands.Register(_commands);
	}

	private ScriptManager _watchedScripts;

	/// <summary>Sends an 'editor' event to every client that subscribed through events.subscribe.</summary>
	public void Emit(string name, object data = null)
	{
		var server = _server;
		if (server == null)
			return;

		try
		{
			server.Broadcast(Serialize(new { @event = "editor", data = new { name, data, time = DateTime.UtcNow } }), c => c.EventsSubscribed);
		}
		catch (Exception)
		{
		}
	}

	private void SubscribeEditorEvents()
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

	private void UnsubscribeEditorEvents()
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
	private void OnProjectLoaded(IGameProject project) => Emit("project.loaded", new { name = project?.ProjectName, path = project?.ProjectPath });
	private void OnProjectUnloaded() => Emit("project.unloaded");
	private void OnCompilationComplete(CompilationResult result, bool _) => Emit("scripts.compiled", new { success = result.Success, errors = result.Errors });

	public override void OnEnabled()
	{
		if (!_options.Enabled || _server != null)
			return;

		try
		{
			_server = new GatewayServer(_options.Port);
			_server.Start();
			Debug.OnLogEntry += ForwardLog;
			SubscribeEditorEvents();
			Debug.Info($"[Gateway] Listening on 127.0.0.1:{_server.Port} (token in {GatewayServer.InfoFilePath})");
		}
		catch (Exception ex)
		{
			Debug.Warn($"[Gateway] Could not start on port {_options.Port}: {ex.Message}");
			_server = null;
		}
	}

	public override void OnDisabled()
	{
		Shutdown();
	}

	public void Shutdown()
	{
		if (_server == null)
			return;

		Debug.OnLogEntry -= ForwardLog;
		UnsubscribeEditorEvents();
		_server.Dispose();
		_server = null;
		Input.Release();
	}

	public override void Update()
	{
		if (_server == null)
			return;

		WatchScripts(_imGui.ScriptManager);

		while (_server.TryDequeue(out var client, out var line))
			Handle(client, line);

		// Nobody left to release the devices: give them back rather than leave the user with a dead mouse.
		if (Input.Captured && _server.ClientCount == 0)
			Input.Release();

		Input.Apply();
		CompletePending();
	}

	/// <summary>Grabs the presented frame, editor UI included. Resolved from <see cref="AfterDraw"/>.</summary>
	public Task<object> CaptureWindow(string path, float scale, Rectangle? crop = null)
	{
		var done = new TaskCompletionSource<object>();
		_captures.Add(new WindowCapture(path, Math.Clamp(scale, 0.05f, 1f), crop, done));
		return done.Task;
	}

	/// <summary>Called by the editor once the frame has been drawn to the back buffer.</summary>
	public void AfterDraw()
	{
		if (_captures.Count == 0)
			return;

		var captures = _captures.ToArray();
		_captures.Clear();

		var device = Core.GraphicsDevice;
		var width = device.PresentationParameters.BackBufferWidth;
		var height = device.PresentationParameters.BackBufferHeight;
		Color[] pixels;
		try
		{
			pixels = new Color[width * height];
			device.GetBackBufferData(pixels);
		}
		catch (Exception ex)
		{
			foreach (var capture in captures)
				capture.Done.TrySetException(new GatewayException($"back buffer read failed: {ex.Message}"));
			return;
		}

		foreach (var capture in captures)
		{
			try
			{
				var (source, sw, sh, crop) = Crop(pixels, width, height, capture.Crop);
				var (data, w, h) = Downscale(source, sw, sh, capture.Scale);
				using var texture = new Texture2D(device, w, h);
				texture.SetData(data);
				using (var stream = File.Create(capture.Path))
					texture.SaveAsPng(stream, w, h);
				capture.Done.TrySetResult(new
				{
					path = capture.Path,
					width = w,
					height = h,
					scale = capture.Scale,
					crop = capture.Crop.HasValue ? new { x = crop.X, y = crop.Y, width = crop.Width, height = crop.Height } : null,
					windowWidth = width,
					windowHeight = height
				});
			}
			catch (Exception ex)
			{
				capture.Done.TrySetException(new GatewayException($"screenshot failed: {ex.Message}"));
			}
		}
	}

	/// <summary>Cuts a window-pixel rectangle out of the frame, clamped to the window.</summary>
	private static (Color[] data, int width, int height, Rectangle rect) Crop(Color[] source, int width, int height, Rectangle? crop)
	{
		if (!crop.HasValue)
			return (source, width, height, new Rectangle(0, 0, width, height));

		var r = Rectangle.Intersect(crop.Value, new Rectangle(0, 0, width, height));
		if (r.Width <= 0 || r.Height <= 0)
			throw new GatewayException("crop rectangle is outside the window");

		var result = new Color[r.Width * r.Height];
		for (var y = 0; y < r.Height; y++)
			Array.Copy(source, (r.Y + y) * width + r.X, result, y * r.Width, r.Width);
		return (result, r.Width, r.Height, r);
	}

	/// <summary>Box-averages into a smaller image so agents pay fewer tokens per frame.</summary>
	private static (Color[] data, int width, int height) Downscale(Color[] source, int width, int height, float scale)
	{
		if (scale >= 0.999f)
			return (source, width, height);

		var w = Math.Max(1, (int)(width * scale));
		var h = Math.Max(1, (int)(height * scale));
		var result = new Color[w * h];

		for (var y = 0; y < h; y++)
		{
			var y0 = y * height / h;
			var y1 = Math.Max(y0 + 1, (y + 1) * height / h);
			for (var x = 0; x < w; x++)
			{
				var x0 = x * width / w;
				var x1 = Math.Max(x0 + 1, (x + 1) * width / w);
				int r = 0, g = 0, b = 0, n = 0;
				for (var sy = y0; sy < y1; sy++)
					for (var sx = x0; sx < x1; sx++)
					{
						var c = source[sy * width + sx];
						r += c.R; g += c.G; b += c.B; n++;
					}
				result[y * w + x] = new Color(r / n, g / n, b / n, 255);
			}
		}

		return (result, w, h);
	}

	private void Handle(GatewayClient client, string line)
	{
		JsonDocument doc;
		try
		{
			doc = JsonDocument.Parse(line);
		}
		catch (JsonException ex)
		{
			client.Send(Serialize(new { ok = false, error = $"invalid JSON: {ex.Message}" }));
			return;
		}

		using (doc)
		{
			var root = doc.RootElement;
			var id = root.TryGetProperty("id", out var idElement) ? idElement.Clone() : default;
			var method = root.TryGetProperty("method", out var m) ? m.GetString() : null;
			var args = new GatewayArgs(root.TryGetProperty("params", out var p) ? p.Clone() : default);

			if (!_commands.TryGet(method, out var command))
			{
				Reply(client, id, false, null, $"unknown method '{method}'");
				return;
			}

			var ctx = new GatewayContext { Client = client, ImGui = _imGui, Server = _server, Commands = _commands, Dispatcher = this };
			try
			{
				var result = command.Handler(args, ctx);
				if (result is Task<object> task)
					_pending.Add(new PendingReply(client, id, task));
				else
					Reply(client, id, true, result, null);
			}
			catch (GatewayException ex)
			{
				Reply(client, id, false, null, ex.Message);
			}
			catch (Exception ex)
			{
				Debug.Warn($"[Gateway] {method} failed: {ex}");
				Reply(client, id, false, null, $"{ex.GetType().Name}: {ex.Message} (stack in log.tail)");
			}
		}
	}

	private void CompletePending()
	{
		for (var i = _pending.Count - 1; i >= 0; i--)
		{
			var entry = _pending[i];
			if (!entry.Task.IsCompleted)
				continue;

			_pending.RemoveAt(i);
			if (entry.Task.IsCompletedSuccessfully)
				Reply(entry.Client, entry.Id, true, entry.Task.Result, null);
			else
				Reply(entry.Client, entry.Id, false, null, entry.Task.Exception?.GetBaseException().Message ?? "cancelled");
		}
	}

	private static void Reply(GatewayClient client, JsonElement id, bool ok, object result, string error)
	{
		object idValue = id.ValueKind == JsonValueKind.Undefined ? null : id;
		client.Send(Serialize(new { id = idValue, ok, result, error }));
	}

	private void ForwardLog(Debug.LogEntry entry)
	{
		var server = _server;
		if (server == null)
			return;

		try
		{
			var json = Serialize(new { @event = "log", data = EditorCommands.LogEntry(entry) });
			server.Broadcast(json, c => c.LogSubscribed);
		}
		catch (Exception)
		{
		}
	}

	/// <summary>Serializes with the gateway's camelCase conventions.</summary>
	public static string Serialize(object value) => JsonSerializer.Serialize(value, JsonOptions);
}
