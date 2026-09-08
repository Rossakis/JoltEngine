using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Voltage.Cli;

/// <summary>Command-line front end for the editor gateway: one process per call, or a long-lived pipe for agents.</summary>
public static class Program
{
	private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

	public static int Main(string[] args)
	{
		try
		{
			return Run(args);
		}
		catch (CliException ex)
		{
			Console.Error.WriteLine($"error: {ex.Message}");
			return 1;
		}
		catch (JsonException ex)
		{
			Console.Error.WriteLine($"error: bad JSON: {ex.Message}");
			return 1;
		}
	}

	private static int Run(string[] argv)
	{
		var args = new List<string>(argv);
		var infoPath = TakeOption(args, "--config") ?? GatewayInfo.DefaultInfoPath();
		var portText = TakeOption(args, "--port");
		var token = TakeOption(args, "--token");
		var timeoutText = TakeOption(args, "--timeout");
		var compact = TakeFlag(args, "--compact");

		if (args.Count == 0 || args[0] is "-h" or "--help")
		{
			PrintUsage();
			return 0;
		}

		if (args[0] == "mcp")
			return new McpServer(infoPath).Run();

		if (args[0] == "start")
		{
			args.RemoveAt(0);
			var exe = TakeOption(args, "--exe");
			var wait = TakeOption(args, "--wait") ?? "120";
			var started = GatewayInfo.Start(infoPath, exe, args.FirstOrDefault(), TimeSpan.FromSeconds(double.Parse(wait, CultureInfo.InvariantCulture)));
			Console.WriteLine(JsonSerializer.Serialize(new { pid = started.Pid, port = started.Port, exe = started.Exe }, Pretty));
			return 0;
		}

		var info = portText != null && token != null
			? new GatewayInfo(int.Parse(portText, CultureInfo.InvariantCulture), token, 0, DateTime.MinValue, null, Array.Empty<string>(), null)
			: GatewayInfo.Load(infoPath);
		if (portText != null)
			info = info with { Port = int.Parse(portText, CultureInfo.InvariantCulture) };
		if (token != null)
			info = info with { Token = token };

		var timeout = timeoutText != null ? TimeSpan.FromSeconds(double.Parse(timeoutText, CultureInfo.InvariantCulture)) : (TimeSpan?)null;
		using var connection = new GatewayConnection(info.Port, info.Token, timeout);

		var command = args[0];
		args.RemoveAt(0);

		switch (command)
		{
			case "help":
				return Help(connection, args.FirstOrDefault(), compact);
			case "logs":
				return Logs(connection, args, compact);
			case "watch":
				return Watch(connection, args);
			case "pipe":
				return Pipe(connection);
			case "--json":
			case "json":
				return SendJson(connection, args.Count > 0 ? args[0] : Console.In.ReadToEnd(), compact);
			default:
				Print(connection.Call(command, BuildParams(args)), compact);
				return 0;
		}
	}

	private static int Help(GatewayConnection connection, string method, bool compact)
	{
		var commands = connection.Call("commands", null);
		if (method == null)
		{
			foreach (var c in commands.EnumerateArray())
				Console.WriteLine($"{c.GetProperty("name").GetString(),-22} {c.GetProperty("help").GetString()}");
			return 0;
		}

		foreach (var c in commands.EnumerateArray())
			if (string.Equals(c.GetProperty("name").GetString(), method, StringComparison.OrdinalIgnoreCase))
			{
				Console.WriteLine(c.GetProperty("help").GetString());
				return 0;
			}

		throw new CliException($"unknown command '{method}'");
	}

	private static int Logs(GatewayConnection connection, List<string> args, bool compact)
	{
		var follow = TakeFlag(args, "--follow") || TakeFlag(args, "-f");
		var level = TakeOption(args, "--level");
		var count = TakeOption(args, "--count") ?? "50";

		var parameters = new JsonObject { ["count"] = int.Parse(count, CultureInfo.InvariantCulture) };
		if (level != null)
			parameters["level"] = level;

		var tail = connection.Call("log.tail", JsonSerializer.SerializeToElement(parameters));
		foreach (var entry in tail.EnumerateArray())
			PrintLog(entry, level);

		if (!follow)
			return 0;

		connection.Call("log.subscribe", null);
		connection.OnEvent = evt =>
		{
			if (evt.GetProperty("event").GetString() == "log")
				PrintLog(evt.GetProperty("data"), level);
		};
		connection.PumpEvents();
		return 0;
	}

	/// <summary>Prints editor lifecycle events, and log entries too with --logs, until the socket closes.</summary>
	private static int Watch(GatewayConnection connection, List<string> args)
	{
		var logs = TakeFlag(args, "--logs");
		connection.Call("events.subscribe", null);
		if (logs)
			connection.Call("log.subscribe", null);

		connection.OnEvent = evt =>
		{
			var kind = evt.GetProperty("event").GetString();
			if (kind == "editor")
			{
				var data = evt.GetProperty("data");
				var payload = data.TryGetProperty("data", out var p) && p.ValueKind != JsonValueKind.Null ? " " + p.GetRawText() : "";
				Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {data.GetProperty("name").GetString()}{payload}");
			}
			else if (kind == "log")
				PrintLog(evt.GetProperty("data"), null);
		};
		connection.PumpEvents();
		return 0;
	}

	/// <summary>Reads one request per stdin line and writes one response per stdout line, for agents that keep the socket warm.</summary>
	private static int Pipe(GatewayConnection connection)
	{
		connection.OnEvent = evt => Console.WriteLine(evt.GetRawText());
		string line;
		while ((line = Console.In.ReadLine()) != null)
		{
			if (string.IsNullOrWhiteSpace(line))
				continue;

			JsonElement? id = null;
			try
			{
				using (var doc = JsonDocument.Parse(line))
					id = doc.RootElement.TryGetProperty("id", out var idElement) ? idElement.Clone() : null;

				var response = connection.SendRaw(line, id.HasValue ? id.Value : null);
				if (id.HasValue)
					Console.WriteLine(JsonSerializer.Serialize(new { id = id.Value, ok = true, result = response.ValueKind == JsonValueKind.Undefined ? (JsonElement?)null : response }));
			}
			catch (Exception ex) when (ex is CliException or JsonException)
			{
				Console.WriteLine(JsonSerializer.Serialize(new { id, ok = false, error = ex.Message }));
			}
		}

		return 0;
	}

	private static int SendJson(GatewayConnection connection, string json, bool compact)
	{
		using var doc = JsonDocument.Parse(json);
		var root = doc.RootElement;
		var method = root.GetProperty("method").GetString();
		var parameters = root.TryGetProperty("params", out var p) ? p.Clone() : (JsonElement?)null;
		Print(connection.Call(method, parameters), compact);
		return 0;
	}

	/// <summary>Turns key=value pairs into a params object; values that look like JSON are passed through as JSON.</summary>
	private static JsonElement? BuildParams(List<string> args)
	{
		if (args.Count == 0)
			return null;

		var obj = new JsonObject();
		foreach (var arg in args)
		{
			var eq = arg.IndexOf('=');
			if (eq <= 0)
				throw new CliException($"expected key=value, got '{arg}'");

			var key = arg.Substring(0, eq);
			var raw = arg.Substring(eq + 1);
			obj[key] = ParseValue(raw);
		}

		return JsonSerializer.SerializeToElement(obj);
	}

	private static JsonNode ParseValue(string raw)
	{
		if (raw == "null")
			return null;
		if (raw == "true")
			return true;
		if (raw == "false")
			return false;
		if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && !raw.StartsWith('+'))
			return raw.Contains('.') || raw.Contains('e') || raw.Contains('E') ? number : (long)number;
		if (raw.Length > 0 && (raw[0] == '[' || raw[0] == '{'))
		{
			try { return JsonNode.Parse(raw); }
			catch (JsonException) { }
		}
		return raw;
	}

	private static void Print(JsonElement result, bool compact)
	{
		if (result.ValueKind == JsonValueKind.Undefined)
			return;
		Console.WriteLine(compact ? result.GetRawText() : JsonSerializer.Serialize(result, Pretty));
	}

	private static void PrintLog(JsonElement entry, string levelFilter)
	{
		var type = entry.GetProperty("type").GetString();
		if (levelFilter != null && !string.Equals(type, levelFilter, StringComparison.OrdinalIgnoreCase))
			return;

		var time = entry.GetProperty("time").GetDateTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
		var caller = entry.GetProperty("caller").GetString();
		var line = entry.GetProperty("line").GetInt32();
		Console.WriteLine($"{time} [{type,-7}] {entry.GetProperty("message").GetString()}  ({caller}:{line})");
	}

	private static string TakeOption(List<string> args, string name)
	{
		var i = args.IndexOf(name);
		if (i >= 0 && i + 1 < args.Count)
		{
			var value = args[i + 1];
			args.RemoveRange(i, 2);
			return value;
		}

		var prefixed = args.FirstOrDefault(a => a.StartsWith(name + "=", StringComparison.Ordinal));
		if (prefixed != null)
		{
			args.Remove(prefixed);
			return prefixed.Substring(name.Length + 1);
		}

		return null;
	}

	private static bool TakeFlag(List<string> args, string name) => args.Remove(name);

	private static void PrintUsage()
	{
		Console.WriteLine(
@"voltage - talk to a running Voltage Editor

usage:
  voltage <method> [key=value ...]     call a gateway method (values may be JSON)
  voltage help [method]                list methods, or describe one
  voltage logs [--follow] [--level L] [--count N]
  voltage watch [--logs]               print scene/project/play/compile events as they happen
  voltage json '{""method"":""..."",""params"":{...}}'
  voltage pipe                         one JSON request per stdin line, one response per stdout line
  voltage mcp                          Model Context Protocol server over stdio (claude mcp add voltage -- voltage mcp)
  voltage start [project.voltage] [--exe <editor exe>] [--wait <sec>]
                                       launch the editor recorded in gateway.json and wait for its gateway
  voltage editor.exit force=true       ask the running editor to quit (force skips the unsaved-changes prompt)

options:
  --config <path>   gateway.json to read (default: the editor's data folder)
  --port <n> --token <t>   connect without gateway.json
  --timeout <sec>   per-request timeout (default 30)
  --compact         print results on one line

examples:
  voltage status
  voltage entity.create name=Player x=100 y=50
  voltage component.add entity=Player type=SpriteRenderer
  voltage component.set entity=Player type=SpriteRenderer member=Color value='{""r"":255,""g"":0,""b"":0,""a"":255}'
  voltage scripts.compile reloadScene=true
  voltage screenshot scale=0.5
  voltage input.click x=40 y=12
  voltage input.type text=Hello
  voltage hotkey.press id=Global.SaveScene");
	}
}
