using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Voltage.Cli;

/// <summary>Model Context Protocol server over stdio that exposes every gateway method as a tool. Logs go to stderr because stdout is the wire.</summary>
public sealed class McpServer
{
	private const string ProtocolVersion = "2024-11-05";

	private static readonly string Instructions =
		"Tools drive a running Voltage Editor. Prefer the semantic tools (entity_*, component_*, scene_*, scripts_compile) over synthetic input; " +
		"they are faster and every edit lands in the editor's undo history. To navigate the UI like a person, call screenshot (scale 0.5 keeps it cheap), " +
		"read pixel coordinates off the image, then input_click / input_drag / input_type / hotkey_press; call input_release when done so the user gets " +
		"their mouse back. Entity numeric ids change on every scene load; use names or guids. Poll log_tail after risky operations.";

	private readonly string _infoPath;
	private readonly string _cachePath;
	private GatewayConnection _connection;
	private List<ToolInfo> _tools;

	private sealed record ToolInfo(string Name, string Method, string Help);

	public McpServer(string infoPath)
	{
		_infoPath = infoPath;
		_cachePath = Path.Combine(Path.GetDirectoryName(infoPath) ?? ".", "gateway-tools.json");
	}

	public int Run()
	{
		var stdin = Console.In;
		var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };

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
				stdout.WriteLine(Error(null, -32700, "parse error"));
				continue;
			}

			var response = Dispatch(message);
			if (response != null)
				stdout.WriteLine(response);
		}

		_connection?.Dispose();
		return 0;
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

		try
		{
			switch (method)
			{
				case "initialize":
					return Result(id, new JsonObject
					{
						["protocolVersion"] = ProtocolVersion,
						["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
						["serverInfo"] = new JsonObject { ["name"] = "voltage-editor", ["version"] = "0.1" },
						["instructions"] = Instructions
					});
				case "notifications/initialized":
				case "notifications/cancelled":
					return null;
				case "ping":
					return Result(id, new JsonObject());
				case "tools/list":
					return Result(id, new JsonObject { ["tools"] = new JsonArray(Tools().Select(ToolSchema).ToArray()) });
				case "tools/call":
					return Result(id, CallTool(parameters?["name"]?.GetValue<string>(), parameters?["arguments"]));
				default:
					return id == null ? null : Error(id, -32601, $"method not found: {method}");
			}
		}
		catch (Exception ex)
		{
			return Error(id, -32603, ex.Message);
		}
	}

	private JsonObject CallTool(string name, JsonNode arguments)
	{
		var tool = Tools().FirstOrDefault(t => t.Name == name);
		if (tool == null)
			return ToolError($"unknown tool '{name}'");

		try
		{
			var connection = Connect();
			var parameters = arguments is JsonObject obj && obj.Count > 0 ? JsonSerializer.SerializeToElement(obj) : (JsonElement?)null;
			var result = connection.Call(tool.Method, parameters);

			var content = new JsonArray();
			if (result.ValueKind != JsonValueKind.Undefined)
				content.Add(new JsonObject { ["type"] = "text", ["text"] = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }) });
			else
				content.Add(new JsonObject { ["type"] = "text", ["text"] = "ok" });

			if (tool.Method == "screenshot" && result.ValueKind == JsonValueKind.Object && result.TryGetProperty("path", out var pathElement))
			{
				var path = pathElement.GetString();
				if (File.Exists(path))
					content.Add(new JsonObject
					{
						["type"] = "image",
						["data"] = Convert.ToBase64String(File.ReadAllBytes(path)),
						["mimeType"] = "image/png"
					});
			}

			return new JsonObject { ["content"] = content };
		}
		catch (CliException ex)
		{
			_connection?.Dispose();
			_connection = null;
			return ToolError(ex.Message);
		}
	}

	private static JsonObject ToolError(string message) => new()
	{
		["isError"] = true,
		["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = message })
	};

	/// <summary>Reconnects on demand so the editor can restart underneath a long-lived MCP session.</summary>
	private GatewayConnection Connect()
	{
		if (_connection != null)
			return _connection;

		var info = GatewayInfo.Load(_infoPath);
		_connection = new GatewayConnection(info.Port, info.Token) { Timeout = TimeSpan.FromMinutes(3) };
		return _connection;
	}

	/// <summary>Live list when the editor is up, else the last list seen, so the tools still exist before the editor starts.</summary>
	private List<ToolInfo> Tools()
	{
		if (_tools != null && _connection != null)
			return _tools;

		try
		{
			var commands = Connect().Call("commands", null);
			_tools = commands.EnumerateArray()
				.Select(c => new ToolInfo(c.GetProperty("name").GetString().Replace('.', '_'), c.GetProperty("name").GetString(), c.GetProperty("help").GetString()))
				.ToList();
			try { File.WriteAllText(_cachePath, JsonSerializer.Serialize(_tools)); } catch (IOException) { }
			return _tools;
		}
		catch (CliException ex)
		{
			Console.Error.WriteLine($"voltage mcp: {ex.Message}");
			_connection?.Dispose();
			_connection = null;
		}

		if (_tools != null)
			return _tools;

		try
		{
			if (File.Exists(_cachePath))
				return _tools = JsonSerializer.Deserialize<List<ToolInfo>>(File.ReadAllText(_cachePath)) ?? new List<ToolInfo>();
		}
		catch (Exception)
		{
		}

		return _tools = new List<ToolInfo>();
	}

	private static readonly Regex ParamPattern = new(@"^([A-Za-z0-9_|]+)\s*(?:=\s*([^\s(]+))?\s*(.*)$", RegexOptions.Compiled);

	/// <summary>Turns the "params: a, b=1 (note)" suffix of a help line into named properties; anything else is still accepted.</summary>
	private static JsonObject ToolSchema(ToolInfo tool)
	{
		var properties = new JsonObject();
		var help = tool.Help ?? "";
		var index = help.IndexOf("params:", StringComparison.OrdinalIgnoreCase);
		if (index >= 0)
		{
			foreach (var raw in SplitParams(help.Substring(index + "params:".Length)))
			{
				var match = ParamPattern.Match(raw.Trim());
				if (!match.Success)
					continue;

				foreach (var name in match.Groups[1].Value.Split('|'))
				{
					var description = match.Groups[3].Value.Trim();
					if (match.Groups[2].Success)
						description = $"default {match.Groups[2].Value}. {description}".Trim();
					properties[name] = string.IsNullOrEmpty(description) ? new JsonObject() : new JsonObject { ["description"] = description };
				}
			}
		}

		return new JsonObject
		{
			["name"] = tool.Name,
			["description"] = help,
			["inputSchema"] = new JsonObject
			{
				["type"] = "object",
				["properties"] = properties,
				["additionalProperties"] = true
			}
		};
	}

	/// <summary>Splits on commas that are not inside brackets or parentheses.</summary>
	private static IEnumerable<string> SplitParams(string text)
	{
		var depth = 0;
		var start = 0;
		for (var i = 0; i < text.Length; i++)
		{
			var c = text[i];
			if (c is '(' or '[' or '{') depth++;
			else if (c is ')' or ']' or '}') depth--;
			else if (c == ',' && depth == 0)
			{
				yield return text.Substring(start, i - start);
				start = i + 1;
			}
		}
		yield return text.Substring(start);
	}

	private static string Result(JsonNode id, JsonNode result) =>
		new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result }.ToJsonString();

	private static string Error(JsonNode id, int code, string message) =>
		new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }.ToJsonString();
}
