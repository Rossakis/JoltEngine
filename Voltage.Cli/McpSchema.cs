using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Voltage.Cli;

/// <summary>One gateway command as the MCP server sees it.</summary>
public sealed record McpTool(string Name, string Method, string Help, JsonElement Params, bool ReadOnly, bool Destructive, bool Unsafe)
{
	/// <summary>Parses the JSON array returned by the gateway's <c>commands</c> method; older cache files without flags still load.</summary>
	public static List<McpTool> Parse(string commandsJson)
	{
		var tools = new List<McpTool>();
		using var doc = JsonDocument.Parse(commandsJson);
		if (doc.RootElement.ValueKind != JsonValueKind.Array)
			return tools;

		foreach (var c in doc.RootElement.EnumerateArray())
		{
			var method = c.TryGetProperty("name", out var n) ? n.GetString() : c.TryGetProperty("Method", out var m) ? m.GetString() : null;
			if (string.IsNullOrEmpty(method))
				continue;

			var help = c.TryGetProperty("help", out var h) ? h.GetString() : c.TryGetProperty("Help", out var h2) ? h2.GetString() : "";
			var parameters = c.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Array ? p.Clone() : default;
			tools.Add(new McpTool(method.Replace('.', '_'), method, help ?? "", parameters, Flag(c, "readOnly"), Flag(c, "destructive"), Flag(c, "unsafe")));
		}

		return tools;
	}

	private static bool Flag(JsonElement element, string name) =>
		element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}

/// <summary>Builds MCP tool definitions: a typed input schema from the declared parameter table, or the legacy help-text parse when a command has none.</summary>
public static class McpSchema
{
	private static readonly Regex ParamPattern = new(@"^([A-Za-z0-9_|]+)\s*(?:=\s*([^\s(]+))?\s*(.*)$", RegexOptions.Compiled);

	public static JsonObject ToolDefinition(McpTool tool)
	{
		var description = tool.Help;
		if (tool.Unsafe)
			description = "⚠ Disabled under --gateway-safe: launches a process, exits or crashes the editor, or writes outside the project. " + description;

		return new JsonObject
		{
			["name"] = tool.Name,
			["title"] = tool.Method,
			["description"] = description,
			["inputSchema"] = tool.Params.ValueKind == JsonValueKind.Array && tool.Params.GetArrayLength() > 0
				? FromParams(tool.Params)
				: FromHelp(tool.Help),
			["annotations"] = new JsonObject
			{
				["title"] = tool.Method,
				["readOnlyHint"] = tool.ReadOnly,
				["destructiveHint"] = tool.Destructive || tool.Unsafe,
				["idempotentHint"] = tool.ReadOnly,
				["openWorldHint"] = false
			}
		};
	}

	private static JsonObject FromParams(JsonElement parameters)
	{
		var properties = new JsonObject();
		var required = new JsonArray();

		foreach (var p in parameters.EnumerateArray())
		{
			var name = p.GetProperty("name").GetString();
			if (string.IsNullOrEmpty(name) || properties.ContainsKey(name))
				continue;

			var type = p.TryGetProperty("type", out var t) ? t.GetString() : "string";
			var schema = new JsonObject();
			if (type != "any")
				schema["type"] = type;
			if (type == "array")
				schema["items"] = new JsonObject { ["type"] = p.TryGetProperty("itemType", out var it) && it.ValueKind == JsonValueKind.String ? it.GetString() : "string" };
			if (p.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String)
				schema["description"] = d.GetString();
			if (p.TryGetProperty("default", out var def) && def.ValueKind != JsonValueKind.Null && def.ValueKind != JsonValueKind.Undefined)
				schema["default"] = JsonNode.Parse(def.GetRawText());
			if (p.TryGetProperty("enum", out var e) && e.ValueKind == JsonValueKind.Array)
				schema["enum"] = JsonNode.Parse(e.GetRawText());

			properties[name] = schema;
			if (p.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.True)
				required.Add(name);
		}

		var result = new JsonObject
		{
			["type"] = "object",
			["properties"] = properties,
			["additionalProperties"] = false
		};
		if (required.Count > 0)
			result["required"] = required;
		return result;
	}

	/// <summary>Turns the "params: a, b=1 (note)" suffix of a help line into named properties; anything else is still accepted.</summary>
	private static JsonObject FromHelp(string help)
	{
		var properties = new JsonObject();
		help ??= "";
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
			["type"] = "object",
			["properties"] = properties,
			["additionalProperties"] = true
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
}
