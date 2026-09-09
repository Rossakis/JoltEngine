using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Voltage.Cli;

/// <summary>Editor state exposed as MCP resources: the last screenshot plus status, commands, log tail and scene.</summary>
public static class McpResources
{
	public const string ScreenshotUri = "voltage://screenshot/latest";
	private const string StatusUri = "voltage://editor/status";
	private const string CommandsUri = "voltage://editor/commands";
	private const string LogUri = "voltage://editor/log";
	private const string SceneUri = "voltage://editor/scene";

	public static JsonArray List(string lastScreenshot)
	{
		var list = new JsonArray
		{
			Entry(StatusUri, "Editor status", "Host, project, scene, play state and frame timing as JSON.", "application/json"),
			Entry(CommandsUri, "Gateway commands", "Every gateway command with its help, parameter table and flags.", "application/json"),
			Entry(LogUri, "Editor log", "The last 200 editor log lines.", "text/plain"),
			Entry(SceneUri, "Open scene", "scene.info plus entity.list for the open scene.", "application/json")
		};
		if (lastScreenshot != null && File.Exists(lastScreenshot))
			list.Add(Entry(ScreenshotUri, "Latest screenshot", $"The most recent capture taken through this server ({Path.GetFileName(lastScreenshot)}).", "image/png"));
		return list;
	}

	/// <summary>One content block for the uri, or null when the uri is unknown.</summary>
	public static JsonObject Read(string uri, Func<GatewayConnection> connect, string lastScreenshot)
	{
		switch (uri)
		{
			case ScreenshotUri:
				if (lastScreenshot == null || !File.Exists(lastScreenshot))
					throw new CliException("no screenshot has been taken yet; call the screenshot tool first");
				return new JsonObject
				{
					["uri"] = uri,
					["mimeType"] = "image/png",
					["blob"] = Convert.ToBase64String(File.ReadAllBytes(lastScreenshot))
				};
			case StatusUri:
				return Text(uri, "application/json", Pretty(connect().Call("status", null)));
			case CommandsUri:
				return Text(uri, "application/json", Pretty(connect().Call("commands", null)));
			case LogUri:
				return Text(uri, "text/plain", FormatLog(connect().Call("log.tail", JsonSerializer.SerializeToElement(new { count = 200 }))));
			case SceneUri:
			{
				var connection = connect();
				var scene = connection.Call("scene.info", null);
				var entities = connection.Call("entity.list", null);
				return Text(uri, "application/json", Pretty(JsonSerializer.SerializeToElement(new { scene, entities })));
			}
			default:
				return null;
		}
	}

	private static JsonObject Entry(string uri, string name, string description, string mimeType) => new()
	{
		["uri"] = uri,
		["name"] = name,
		["description"] = description,
		["mimeType"] = mimeType
	};

	private static JsonObject Text(string uri, string mimeType, string text) => new()
	{
		["uri"] = uri,
		["mimeType"] = mimeType,
		["text"] = text
	};

	private static string Pretty(JsonElement element) =>
		element.ValueKind == JsonValueKind.Undefined ? "null" : JsonSerializer.Serialize(element, new JsonSerializerOptions { WriteIndented = true });

	private static string FormatLog(JsonElement entries)
	{
		var sb = new StringBuilder();
		if (entries.ValueKind != JsonValueKind.Array)
			return sb.ToString();

		foreach (var entry in entries.EnumerateArray())
		{
			var time = entry.TryGetProperty("time", out var t) && t.TryGetDateTime(out var when) ? when.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) : "";
			var type = entry.TryGetProperty("type", out var ty) ? ty.GetString() : "";
			var message = entry.TryGetProperty("message", out var m) ? m.GetString() : "";
			var caller = entry.TryGetProperty("caller", out var c) ? c.GetString() : "";
			var line = entry.TryGetProperty("line", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : 0;
			sb.Append(time).Append(" [").Append(type).Append("] ").Append(message).Append("  (").Append(caller).Append(':').Append(line).AppendLine(")");
		}

		return sb.ToString();
	}
}
