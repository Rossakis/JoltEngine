using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Voltage.Gateway.Commands;
using Voltage.Utils;

namespace Voltage.Gateway;

/// <summary>Drains gateway requests on the main thread each frame and answers them. Hosts subclass it to add commands and events.</summary>
public class GatewayDispatcher : GlobalManager
{
	/// <summary>Games built with PublishAot turn reflection serialization off by default, so the resolver is explicit.</summary>
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		IncludeFields = true,
		NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
		Converters = { new JsonStringEnumConverter() }
	};

	private readonly GatewayCommandTable _commands = new();
	private readonly List<PendingReply> _pending = new();
	private readonly List<WindowCapture> _captures = new();
	private readonly List<EventWaiter> _waiters = new();
	private readonly List<BatchRun> _batches = new();
	private readonly System.Collections.Concurrent.ConcurrentQueue<(string name, object data)> _offThreadEvents = new();
	private readonly int _mainThreadId = Environment.CurrentManagedThreadId;
	private GatewayServer _server;

	private sealed record PendingReply(GatewayClient Client, JsonElement Id, Task<object> Task);

	private sealed record WindowCapture(string Path, float Scale, Rectangle? Crop, TaskCompletionSource<object> Done);

	private sealed record EventWaiter(string Pattern, DateTime Deadline, TaskCompletionSource<object> Done);

	public GatewayOptions Options { get; }

	public GatewayCommandTable Commands => _commands;

	public GatewayServer Server => _server;

	public InputSimulator Input { get; } = new();

	/// <summary>Where screenshots land when no path is given; the only writable place under --gateway-safe.</summary>
	public virtual string ScreenshotDirectory => Path.Combine(GatewayStorage.RuntimeRoot, "Screenshots");

	public GatewayDispatcher(GatewayOptions options)
	{
		Options = options;
		Input.EventWaiter = WaitForEvent;
		CoreCommands.Register(_commands);
	}

	/// <summary>Sends an 'editor' event to every client that subscribed through events.subscribe, and wakes matching wait.event callers.</summary>
	public void Emit(string name, object data = null)
	{
		if (Environment.CurrentManagedThreadId != _mainThreadId)
		{
			_offThreadEvents.Enqueue((name, data));
			return;
		}

		for (var i = _waiters.Count - 1; i >= 0; i--)
		{
			if (!Matches(_waiters[i].Pattern, name))
				continue;
			var waiter = _waiters[i];
			_waiters.RemoveAt(i);
			waiter.Done.TrySetResult(new { name, data });
		}

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

	/// <summary>Completes with {name, data} on the next event matching the pattern ("scene.loaded", "scene.*", "*"), or fails after the timeout.</summary>
	public Task<object> WaitForEvent(string pattern, TimeSpan timeout)
	{
		if (string.IsNullOrWhiteSpace(pattern))
			throw new GatewayException("missing event name");
		var waiter = new EventWaiter(pattern.Trim(), DateTime.UtcNow + timeout, new TaskCompletionSource<object>());
		_waiters.Add(waiter);
		return waiter.Done.Task;
	}

	private static bool Matches(string pattern, string name)
	{
		if (pattern == "*")
			return true;
		if (pattern.EndsWith(".*", StringComparison.Ordinal))
			return name.StartsWith(pattern.Substring(0, pattern.Length - 1), StringComparison.OrdinalIgnoreCase);
		return string.Equals(pattern, name, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>Runs requests in order, waiting for deferred answers, and replies once with every result.</summary>
	public Task<object> RunBatch(GatewayContext ctx, List<(string method, JsonElement args)> requests, bool stopOnError)
	{
		var run = new BatchRun(this, ctx, requests, stopOnError);
		_batches.Add(run);
		run.Step();
		return run.Task;
	}

	/// <summary>Runs the host's subscriptions once the server listens.</summary>
	protected virtual void OnStarted()
	{
	}

	/// <summary>Mirror of <see cref="OnStarted"/>, before the server closes.</summary>
	protected virtual void OnStopping()
	{
	}

	/// <summary>Hosts attach their own per-request services here.</summary>
	protected virtual GatewayContext CreateContext(GatewayClient client) =>
		new() { Client = client, Server = _server, Commands = _commands, Dispatcher = this };

	public override void OnEnabled()
	{
		if (!Options.Enabled || _server != null)
			return;

		try
		{
			_server = new GatewayServer(Options);
			_server.Start();
			Debug.OnLogEntry += ForwardLog;
			OnStarted();
			Debug.Info($"[Gateway] Listening on 127.0.0.1:{_server.Port} (token in {_server.InfoFilePath}){(Options.Safe ? " in safe mode" : "")}");
		}
		catch (Exception ex)
		{
			Debug.Warn($"[Gateway] Could not start on port {Options.Port}: {ex.Message}");
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
		OnStopping();
		_server.Dispose();
		_server = null;
		Input.Release();
	}

	public override void Update()
	{
		if (_server == null)
			return;

		while (_offThreadEvents.TryDequeue(out var queued))
			Emit(queued.name, queued.data);

		while (_server.TryDequeue(out var client, out var line))
			Handle(client, line);

		// Nobody left to release the devices: give them back rather than leave the user with a dead mouse.
		if (Input.Captured && _server.ClientCount == 0)
			Input.Release();

		Input.Apply();
		ExpireWaiters();
		for (var i = _batches.Count - 1; i >= 0; i--)
			if (_batches[i].Step())
				_batches.RemoveAt(i);
		CompletePending();
	}

	private void ExpireWaiters()
	{
		if (_waiters.Count == 0)
			return;
		var now = DateTime.UtcNow;
		for (var i = _waiters.Count - 1; i >= 0; i--)
		{
			if (_waiters[i].Deadline > now)
				continue;
			var waiter = _waiters[i];
			_waiters.RemoveAt(i);
			waiter.Done.TrySetException(new GatewayException($"no '{waiter.Pattern}' event arrived in time"));
		}
	}

	/// <summary>Grabs the presented frame, UI included. Resolved from <see cref="AfterDraw"/>.</summary>
	public Task<object> CaptureWindow(string path, float scale, Rectangle? crop = null)
	{
		var done = new TaskCompletionSource<object>();
		_captures.Add(new WindowCapture(path, Math.Clamp(scale, 0.05f, 1f), crop, done));
		return done.Task;
	}

	/// <summary>Called by the host once the frame has been drawn to the back buffer.</summary>
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
				Directory.CreateDirectory(Path.GetDirectoryName(capture.Path)!);
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
	internal static (Color[] data, int width, int height) Downscale(Color[] source, int width, int height, float scale)
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
			if (root.ValueKind != JsonValueKind.Object)
			{
				client.Send(Serialize(new { ok = false, error = "request must be a JSON object with method and params" }));
				return;
			}

			var id = root.TryGetProperty("id", out var idElement) ? idElement.Clone() : default;
			var method = root.TryGetProperty("method", out var m) ? m.GetString() : null;
			var args = root.TryGetProperty("params", out var p) ? p.Clone() : default;

			var result = Invoke(CreateContext(client), method, args, out var error);
			if (error != null)
				Reply(client, id, false, null, error);
			else if (result is Task<object> task)
				_pending.Add(new PendingReply(client, id, task));
			else
				Reply(client, id, true, result, null);
		}
	}

	/// <summary>Runs one command; a user-facing failure comes back as the error string, a handler Task as the result.</summary>
	internal object Invoke(GatewayContext ctx, string method, JsonElement args, out string error)
	{
		error = null;
		if (!_commands.TryGet(method, out var command))
		{
			error = $"unknown method '{method}'";
			return null;
		}

		if (Options.Safe && command.IsUnsafe)
		{
			error = $"'{command.Name}' is disabled by --gateway-safe";
			return null;
		}

		try
		{
			return command.Handler(new GatewayArgs(args), ctx);
		}
		catch (GatewayException ex)
		{
			error = ex.Message;
		}
		catch (Exception ex)
		{
			Debug.Warn($"[Gateway] {method} failed: {ex}");
			error = $"{ex.GetType().Name}: {ex.Message} (stack in log.tail)";
		}

		return null;
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
			var json = Serialize(new { @event = "log", data = CoreCommands.LogEntry(entry) });
			server.Broadcast(json, c => c.LogSubscribed);
		}
		catch (Exception)
		{
		}
	}

	/// <summary>Serializes with the gateway's camelCase conventions.</summary>
	public static string Serialize(object value) => JsonSerializer.Serialize(value, JsonOptions);

	/// <summary>One in-flight 'batch' request: steps through its requests as each deferred answer lands.</summary>
	private sealed class BatchRun
	{
		private readonly GatewayDispatcher _dispatcher;
		private readonly GatewayContext _ctx;
		private readonly List<(string method, JsonElement args)> _requests;
		private readonly bool _stopOnError;
		private readonly List<object> _results = new();
		private readonly TaskCompletionSource<object> _done = new();
		private Task<object> _current;
		private int _next;
		private int? _failed;

		public Task<object> Task => _done.Task;

		public BatchRun(GatewayDispatcher dispatcher, GatewayContext ctx, List<(string method, JsonElement args)> requests, bool stopOnError)
		{
			_dispatcher = dispatcher;
			_ctx = ctx;
			_requests = requests;
			_stopOnError = stopOnError;
		}

		/// <summary>Advances as far as possible this frame; true once the batch has answered.</summary>
		public bool Step()
		{
			while (true)
			{
				if (_current != null)
				{
					if (!_current.IsCompleted)
						return false;
					if (_current.IsCompletedSuccessfully)
						_results.Add(new { ok = true, result = _current.Result });
					else
						Fail(_current.Exception?.GetBaseException().Message ?? "cancelled");
					_current = null;
					if (_done.Task.IsCompleted)
						return true;
				}

				if (_next >= _requests.Count)
				{
					_done.TrySetResult(new { results = _results, failed = _failed });
					return true;
				}

				var (method, args) = _requests[_next++];
				var result = _dispatcher.Invoke(_ctx, method, args, out var error);
				if (error != null)
				{
					Fail(error);
					if (_done.Task.IsCompleted)
						return true;
				}
				else if (result is Task<object> task)
					_current = task;
				else
					_results.Add(new { ok = true, result });
			}
		}

		private void Fail(string error)
		{
			_results.Add(new { ok = false, error });
			_failed ??= _results.Count - 1;
			if (_stopOnError)
				_done.TrySetResult(new { results = _results, failed = _failed });
		}
	}
}
