using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Voltage.Cli;

/// <summary>Model Context Protocol server over stdio that exposes every gateway method as a tool and the editor's state as resources. Logs go to stderr because stdout is the wire.</summary>
public sealed class McpServer
{
	private const string DefaultProtocolVersion = "2025-06-18";
	private static readonly string[] SupportedProtocolVersions = { "2024-11-05", "2025-03-26", "2025-06-18" };
	private const int MaxTextLength = 50_000;

	private static readonly string Instructions =
		"Tools drive a running Voltage Editor. Prefer the semantic tools (entity_*, component_*, scene_*, scripts_compile) over synthetic input; " +
		"they are faster and every edit lands in the editor's undo history. To navigate the UI like a person, use ui_tree / ui_find to read the visible widgets " +
		"and ui_click label= to press one; fall back to screenshot (scale 0.5 keeps it cheap) plus input_click / input_drag / input_type / hotkey_press only for " +
		"things without a label, and call input_release when done so the user gets their mouse back. batch runs several requests in one call with a single undo " +
		"group; wait_event blocks until an editor event such as scene.loaded or scripts.compiled fires. The resource voltage://screenshot/latest holds the last " +
		"capture. Entity numeric ids change on every scene load; use names or guids. Poll log_tail after risky operations.";

	private readonly string _infoPath;
	private readonly string _cachePath;
	private readonly object _sync = new();
	private readonly object _stdoutLock = new();
	private StreamWriter _stdout;
	private GatewayConnection _connection;
	private List<McpTool> _tools;
	private string _lastScreenshot;

	public McpServer(string infoPath)
	{
		_infoPath = infoPath;
		_cachePath = Path.Combine(Path.GetDirectoryName(infoPath) ?? ".", "gateway-tools.json");
	}

	public int Run()
	{
		var stdin = Console.In;
		_stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };

		string line;
		while ((line = stdin.ReadLine()) != null)
		{
			if (string.IsNullOrWhiteSpace(line))
				continue;

			JsonNode message;
			try
			{
				message = JsonNode.Parse(line);
			}
			catch (JsonException)
			{
				Send(Error(null, -32700, "parse error"));
				continue;
			}

			var response = Dispatch(message);
			if (response != null)
				Send(response);
		}

		lock (_sync)
		{
			_connection?.Dispose();
			_connection = null;
		}
		return 0;
	}

	private void Send(string json)
	{
		lock (_stdoutLock)
			_stdout.WriteLine(json);
	}

	private string Dispatch(JsonNode message)
	{
		if (message is not JsonObject request)
			return Error(null, -32600, "batch requests are not supported");

		var id = request["id"];
		JsonNode parameters;
		string method;
		try
		{
			method = request["method"]?.GetValue<string>();
			parameters = request["params"];
		}
		catch (Exception)
		{
			return Error(id, -32600, "invalid request");
		}

		if (method != null && method.StartsWith("notifications/", StringComparison.Ordinal))
			return null;

		try
		{
			switch (method)
			{
				case "initialize":
					return Result(id, Initialize(parameters));
				case "ping":
					return Result(id, new JsonObject());
				case "tools/list":
					return Result(id, new JsonObject { ["tools"] = new JsonArray(Tools().Select(McpSchema.ToolDefinition).ToArray()) });
				case "tools/call":
					return Result(id, CallTool(parameters?["name"]?.GetValue<string>(), parameters?["arguments"]));
				case "resources/list":
					return Result(id, new JsonObject { ["resources"] = McpResources.List(_lastScreenshot) });
				case "resources/templates/list":
					return Result(id, new JsonObject { ["resourceTemplates"] = new JsonArray() });
				case "resources/read":
					return ReadResource(id, parameters?["uri"]?.GetValue<string>());
				default:
					return id == null ? null : Error(id, -32601, $"method not found: {method}");
			}
		}
		catch (Exception ex)
		{
			return Error(id, -32603, ex.Message);
		}
	}

	private JsonObject Initialize(JsonNode parameters)
	{
		var requested = parameters?["protocolVersion"]?.GetValue<string>();
		var version = requested != null && SupportedProtocolVersions.Contains(requested) ? requested : DefaultProtocolVersion;
		return new JsonObject
		{
			["protocolVersion"] = version,
			["capabilities"] = new JsonObject
			{
				["tools"] = new JsonObject { ["listChanged"] = true },
				["resources"] = new JsonObject { ["subscribe"] = false, ["listChanged"] = false }
			},
			["serverInfo"] = new JsonObject { ["name"] = "voltage-editor", ["version"] = "0.2" },
			["instructions"] = Instructions
		};
	}

	private JsonObject CallTool(string name, JsonNode arguments)
	{
		var tool = Tools().FirstOrDefault(t => t.Name == name);
		if (tool == null)
			return ToolError($"unknown tool '{name}'");

		try
		{
			var parameters = arguments is JsonObject obj && obj.Count > 0 ? JsonSerializer.SerializeToElement(obj) : (JsonElement?)null;
			var result = Connect().Call(tool.Method, parameters);

			var content = new JsonArray();
			var text = result.ValueKind == JsonValueKind.Undefined ? "ok" : JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
			if (text.Length > MaxTextLength)
				text = text.Substring(0, MaxTextLength) + $"\n… truncated ({text.Length - MaxTextLength} more characters); narrow the request with filters or paging.";

			if (tool.Method == "screenshot" && result.ValueKind == JsonValueKind.Object && result.TryGetProperty("path", out var pathElement))
			{
				var path = pathElement.GetString();
				if (File.Exists(path))
				{
					_lastScreenshot = path;
					text += $"\nAlso available as resource {McpResources.ScreenshotUri}.";
					content.Add(new JsonObject { ["type"] = "text", ["text"] = text });
					content.Add(new JsonObject
					{
						["type"] = "image",
						["data"] = Convert.ToBase64String(File.ReadAllBytes(path)),
						["mimeType"] = "image/png"
					});
					return new JsonObject { ["content"] = content };
				}
			}

			content.Add(new JsonObject { ["type"] = "text", ["text"] = text });
			return new JsonObject { ["content"] = content };
		}
		catch (CliException ex)
		{
			DropConnection();
			return ToolError(ex.Message);
		}
	}

	private string ReadResource(JsonNode id, string uri)
	{
		if (string.IsNullOrEmpty(uri))
			return Error(id, -32602, "missing uri");

		try
		{
			var contents = McpResources.Read(uri, Connect, _lastScreenshot);
			if (contents == null)
				return Error(id, -32002, $"unknown resource '{uri}'");
			return Result(id, new JsonObject { ["contents"] = new JsonArray(contents) });
		}
		catch (CliException ex)
		{
			DropConnection();
			return Error(id, -32002, ex.Message);
		}
	}

	private static JsonObject ToolError(string message) => new()
	{
		["isError"] = true,
		["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = message })
	};

	/// <summary>Reconnects on demand so the editor can restart underneath a long-lived MCP session; the fresh socket subscribes to editor events.</summary>
	private GatewayConnection Connect()
	{
		lock (_sync)
		{
			if (_connection != null && _connection.IsOpen)
				return _connection;

			_connection?.Dispose();
			var info = GatewayInfo.Load(_infoPath);
			var connection = new GatewayConnection(info.Port, info.Token) { Timeout = TimeSpan.FromMinutes(3) };
			connection.OnEvent = OnEditorEvent;
			_connection = connection;
			try { connection.Call("events.subscribe", null); } catch (CliException) { }
			return connection;
		}
	}

	private void DropConnection()
	{
		lock (_sync)
		{
			_connection?.Dispose();
			_connection = null;
		}
	}

	/// <summary>Runs on the connection's reader thread, so the refresh is handed off before it calls back into the gateway.</summary>
	private void OnEditorEvent(JsonElement evt)
	{
		if (evt.GetProperty("event").GetString() != "editor" || !evt.TryGetProperty("data", out var data))
			return;

		var name = data.TryGetProperty("name", out var n) ? n.GetString() : null;
		if (name is "scripts.compiled" or "project.loaded")
			Task.Run(RefreshTools);
	}

	private void RefreshTools()
	{
		lock (_sync)
			_tools = null;

		var tools = Tools();
		if (tools.Count > 0)
			Send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/tools/list_changed" }.ToJsonString());
	}

	/// <summary>Live list when the editor is up, else the last list seen, so the tools still exist before the editor starts.</summary>
	private List<McpTool> Tools()
	{
		lock (_sync)
		{
			if (_tools != null && _connection != null && _connection.IsOpen)
				return _tools;
		}

		try
		{
			var commands = Connect().Call("commands", null);
			var raw = commands.GetRawText();
			var tools = McpTool.Parse(raw);
			try { File.WriteAllText(_cachePath, raw); } catch (IOException) { }
			lock (_sync)
				return _tools = tools;
		}
		catch (CliException ex)
		{
			Console.Error.WriteLine($"voltage mcp: {ex.Message}");
			DropConnection();
		}

		lock (_sync)
		{
			if (_tools != null)
				return _tools;

			try
			{
				if (File.Exists(_cachePath))
					return _tools = McpTool.Parse(File.ReadAllText(_cachePath));
			}
			catch (Exception)
			{
			}

			return _tools = new List<McpTool>();
		}
	}

	private static string Result(JsonNode id, JsonNode result) =>
		new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result }.ToJsonString();

	private static string Error(JsonNode id, int code, string message) =>
		new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }.ToJsonString();
}
