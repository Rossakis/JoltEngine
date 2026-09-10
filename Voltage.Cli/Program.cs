using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Voltage.Cli;

/// <summary>Command-line front end for the editor and game gateways: one process per call, or a long-lived pipe for agents.</summary>
public static class Program
{
	private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

	/// <summary>How results print: JSON by default, one line with --compact, a table with --table, one value with --field.</summary>
	private sealed record Output(bool Compact, bool Table, string Field);

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
		var game = TakeFlag(args, "--game") || string.Equals(Environment.GetEnvironmentVariable("VOLTAGE_TARGET"), "game", StringComparison.OrdinalIgnoreCase);
		var host = game ? "game" : "editor";
		var configOverride = TakeOption(args, "--config");
		var infoPath = configOverride ?? GatewayInfo.DefaultInfoPath(host);
		var portText = TakeOption(args, "--port");
		var token = TakeOption(args, "--token");
		var timeoutText = TakeOption(args, "--timeout");
		var output = new Output(TakeFlag(args, "--compact"), TakeFlag(args, "--table"), TakeOption(args, "--field"));

		if (args.Count == 0 || args[0] is "-h" or "--help")
		{
			PrintUsage();
			return 0;
		}

		switch (args[0])
		{
			case "--version":
			case "-V":
				return Lifecycle.Version(infoPath);
			case "stop":
				args.RemoveAt(0);
				return Lifecycle.Stop(infoPath, game, TimeSpan.FromSeconds(double.Parse(TakeOption(args, "--wait") ?? "15", CultureInfo.InvariantCulture)));
			case "restart":
			{
				args.RemoveAt(0);
				var rebuild = TakeFlag(args, "--rebuild");
				var wait = TimeSpan.FromSeconds(double.Parse(TakeOption(args, "--wait") ?? "120", CultureInfo.InvariantCulture));
				var passthrough = Lifecycle.TakePassthrough(args, TakeOption, TakeFlag);
				return Lifecycle.Restart(infoPath, game, rebuild, wait, args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)), passthrough);
			}
			case "mcp":
				return new McpServer(infoPath).Run();
			case "doctor":
				args.RemoveAt(0);
				return Doctor.Run(game ? GatewayInfo.DefaultInfoPath("editor") : infoPath, game ? infoPath : GatewayInfo.DefaultInfoPath("game"), TakeFlag(args, "--json"));
			case "completion":
				return Completion.Script(args.Count > 1 ? args[1] : null, configOverride);
			case "__complete":
				args.RemoveAt(0);
				return Completion.Complete(args, infoPath);
			case "start":
			{
				args.RemoveAt(0);
				var exe = TakeOption(args, "--exe");
				var wait = TimeSpan.FromSeconds(double.Parse(TakeOption(args, "--wait") ?? "120", CultureInfo.InvariantCulture));
				var passthrough = Lifecycle.TakePassthrough(args, TakeOption, TakeFlag);
				var started = game
					? GatewayInfo.StartGame(infoPath, exe ?? args.FirstOrDefault(), wait)
					: GatewayInfo.Start(infoPath, exe, args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)), wait, passthrough);
				Console.WriteLine(JsonSerializer.Serialize(new { pid = started.Pid, port = started.Port, exe = started.Exe, host = started.Host, game = started.Game, info = infoPath }, Pretty));
				return 0;
			}
		}

		var info = portText != null && token != null
			? new GatewayInfo(int.Parse(portText, CultureInfo.InvariantCulture), token, 0, DateTime.MinValue, null, Array.Empty<string>(), null, host, null)
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
				return Help(connection, args.FirstOrDefault());
			case "logs":
				return Logs(connection, args);
			case "watch":
				return Watch(connection, args);
			case "pipe":
				return Pipe(connection);
			case "record":
				return Recorder.Record(connection, args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)), !TakeFlag(args, "--no-mouse"), !TakeFlag(args, "--no-keyboard"), !TakeFlag(args, "--no-text"));
			case "run":
			{
				var batch = TakeFlag(args, "--batch");
				var stopOnError = !TakeFlag(args, "--continue");
				return ScriptRunner.Run(connection, args.FirstOrDefault(), batch, stopOnError, r =>
				{
					try { Print(r, output); }
					catch (CliException ex) { Console.WriteLine($"({ex.Message})"); }
				});
			}
			case "--json":
			case "json":
				return SendJson(connection, args.Count > 0 ? args[0] : Console.In.ReadToEnd(), output);
			default:
				Print(connection.Call(command, BuildParams(args)), output);
				return 0;
		}
	}

	private static int Help(GatewayConnection connection, string method)
	{
		var commands = connection.Call("commands", null);
		if (method == null)
		{
			foreach (var c in commands.EnumerateArray())
				Console.WriteLine($"{c.GetProperty("name").GetString(),-24} {Flags(c)}{c.GetProperty("help").GetString()}");
			return 0;
		}

		foreach (var c in commands.EnumerateArray())
			if (string.Equals(c.GetProperty("name").GetString(), method, StringComparison.OrdinalIgnoreCase))
			{
				Console.WriteLine(c.GetProperty("help").GetString());
				if (c.TryGetProperty("params", out var parameters) && parameters.ValueKind == JsonValueKind.Array && parameters.GetArrayLength() > 0)
				{
					Console.WriteLine();
					foreach (var p in parameters.EnumerateArray())
					{
						var name = p.GetProperty("name").GetString();
						var type = p.TryGetProperty("type", out var t) ? t.GetString() : "any";
						var required = p.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.True ? " (required)" : "";
						var fallback = p.TryGetProperty("default", out var d) && d.ValueKind != JsonValueKind.Null ? $" = {TableWriter.Cell(d)}" : "";
						var description = p.TryGetProperty("description", out var desc) && desc.ValueKind == JsonValueKind.String ? "  " + desc.GetString() : "";
						Console.WriteLine($"  {name,-16} {type}{fallback}{required}{description}");
					}
				}
				var flags = Flags(c);
				if (flags.Length > 0)
					Console.WriteLine($"\n  flags: {flags.Trim()}");
				return 0;
			}

		throw new CliException($"unknown command '{method}'");
	}

	private static string Flags(JsonElement command)
	{
		var flags = new List<string>();
		if (command.TryGetProperty("readOnly", out var ro) && ro.ValueKind == JsonValueKind.True) flags.Add("read-only");
		if (command.TryGetProperty("destructive", out var d) && d.ValueKind == JsonValueKind.True) flags.Add("destructive");
		if (command.TryGetProperty("unsafe", out var u) && u.ValueKind == JsonValueKind.True) flags.Add("unsafe");
		return flags.Count == 0 ? "" : $"[{string.Join(", ", flags)}] ";
	}

	private static int Logs(GatewayConnection connection, List<string> args)
	{
		var follow = TakeFlag(args, "--follow") || TakeFlag(args, "-f");
		var level = TakeOption(args, "--level");
		var count = TakeOption(args, "--count") ?? "50";
		var grepText = TakeOption(args, "--grep");
		var sinceText = TakeOption(args, "--since");
		var grep = grepText == null ? null : new Regex(grepText, RegexOptions.IgnoreCase);
		var since = sinceText == null ? (DateTime?)null : DateTime.UtcNow - TimeSpan.FromSeconds(double.Parse(sinceText, CultureInfo.InvariantCulture));

		var parameters = new JsonObject { ["count"] = int.Parse(count, CultureInfo.InvariantCulture) };
		if (level != null)
			parameters["level"] = level;

		var tail = connection.Call("log.tail", JsonSerializer.SerializeToElement(parameters));
		foreach (var entry in tail.EnumerateArray())
			if (LogMatches(entry, grep, since))
				PrintLog(entry, level);

		if (!follow)
			return 0;

		connection.Call("log.subscribe", null);
		connection.OnEvent = evt =>
		{
			if (evt.GetProperty("event").GetString() == "log" && LogMatches(evt.GetProperty("data"), grep, since))
				PrintLog(evt.GetProperty("data"), level);
		};
		connection.PumpEvents();
		return 0;
	}

	/// <summary>Prints lifecycle events (and log entries with --logs) until the socket closes; --filter narrows event names, --json prints raw lines.</summary>
	private static int Watch(GatewayConnection connection, List<string> args)
	{
		var logs = TakeFlag(args, "--logs");
		var raw = TakeFlag(args, "--json");
		var filter = Glob(TakeOption(args, "--filter"));

		connection.Call("events.subscribe", null);
		if (logs)
			connection.Call("log.subscribe", null);

		connection.OnEvent = evt =>
		{
			var kind = evt.GetProperty("event").GetString();
			if (kind == "editor")
			{
				var data = evt.GetProperty("data");
				var name = data.GetProperty("name").GetString() ?? "";
				if (filter != null && !filter.IsMatch(name))
					return;
				if (raw)
				{
					Console.WriteLine(evt.GetRawText());
					return;
				}
				var payload = data.TryGetProperty("data", out var p) && p.ValueKind != JsonValueKind.Null ? " " + p.GetRawText() : "";
				Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {name}{payload}");
			}
			else if (kind == "log")
			{
				if (raw)
					Console.WriteLine(evt.GetRawText());
				else
					PrintLog(evt.GetProperty("data"), null);
			}
		};
		connection.PumpEvents();
		return 0;
	}

	/// <summary>"scene.*" style pattern to a regex; null passes everything.</summary>
	private static Regex Glob(string pattern)
	{
		if (string.IsNullOrEmpty(pattern))
			return null;
		return new Regex("^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase);
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

	private static int SendJson(GatewayConnection connection, string json, Output output)
	{
		using var doc = JsonDocument.Parse(json);
		var root = doc.RootElement;
		var method = root.GetProperty("method").GetString();
		var parameters = root.TryGetProperty("params", out var p) ? p.Clone() : (JsonElement?)null;
		Print(connection.Call(method, parameters), output);
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

	private static void Print(JsonElement result, Output output)
	{
		if (result.ValueKind == JsonValueKind.Undefined)
			return;

		if (output.Field != null)
		{
			var picked = TableWriter.Select(result, output.Field) ?? throw new CliException($"no value at '{output.Field}'");
			Console.WriteLine(picked.ValueKind == JsonValueKind.String ? picked.GetString() : Serialize(picked, output.Compact));
			return;
		}

		if (output.Table)
		{
			var text = new StringBuilder();
			if (TableWriter.TryWrite(result, text))
			{
				Console.Write(text.ToString());
				return;
			}
		}

		Console.WriteLine(Serialize(result, output.Compact));
	}

	private static string Serialize(JsonElement value, bool compact) => compact ? value.GetRawText() : JsonSerializer.Serialize(value, Pretty);

	/// <summary>--grep matches the message or caller; --since drops entries older than the cutoff.</summary>
	private static bool LogMatches(JsonElement entry, Regex grep, DateTime? since)
	{
		if (since.HasValue && entry.TryGetProperty("time", out var time) && time.TryGetDateTime(out var when) && when.ToUniversalTime() < since.Value)
			return false;
		if (grep == null)
			return true;
		var message = entry.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
		var caller = entry.TryGetProperty("caller", out var c) ? c.GetString() ?? "" : "";
		return grep.IsMatch(message) || grep.IsMatch(caller);
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
@"voltage - talk to a running Voltage Editor, or to a built game started with --gateway

usage:
  voltage <method> [key=value ...]     call a gateway method (values may be JSON)
  voltage help [method]                list methods with their flags, or describe one with its parameters
  voltage doctor [--json]              check gateway.json, the editor/game process, the port, the build and the SDK
  voltage logs [--follow] [--level L] [--count N] [--grep regex] [--since sec]
  voltage watch [--logs] [--filter scene.*] [--json]
                                       print lifecycle events (and logs) as they happen
  voltage record <script.json> [--no-mouse] [--no-keyboard] [--no-text]
                                       record the mouse, keyboard and typed text until Enter, as a script for voltage run
  voltage run <script.json> [--batch] [--continue]
                                       replay {""steps"":[...]} through input.script, or a list of {method, params} calls
  voltage json '{""method"":""..."",""params"":{...}}'
  voltage pipe                         one JSON request per stdin line, one response per stdout line
  voltage mcp                          Model Context Protocol server over stdio (claude mcp add voltage -- voltage mcp)
  voltage start [project.voltage] [--exe <editor exe>] [--wait <sec>] [--headless] [--safe] [--no-prompts] [--gateway-port N]
                                       launch the editor recorded in gateway.json and wait for its gateway
  voltage start --game <game exe>      launch a built game with its gateway on and wait for it
  voltage stop [--game] [--wait <sec>] ask the editor (or game) to quit and wait until it is gone
  voltage restart [--rebuild] [same flags as start]
                                       stop, optionally dotnet build the editor project next to its exe, and start again
  voltage --version                    print the CLI version and the running host's engine version
  voltage completion bash|zsh|pwsh     print a shell completion script (methods and key= names complete live)
  voltage editor.exit force=true       ask the running editor to quit (force skips the unsaved-changes prompt)
  voltage --game app.exit              ask the running game to quit

options:
  --game            talk to the game gateway (runtime gateway.json) instead of the editor; VOLTAGE_TARGET=game does the same
  --config <path>   gateway.json to read (default: the editor's or game's data folder)
  --port <n> --token <t>   connect without gateway.json
  --timeout <sec>   per-request timeout (default 30)
  --compact         print results on one line
  --table           print lists of objects as an aligned table
  --field <a.b.0>   print one value out of the result

examples:
  voltage status
  voltage entity.list --table
  voltage status --field scene.name
  voltage entity.create name=Player x=100 y=50
  voltage component.add entity=Player type=SpriteRenderer
  voltage component.set entity=Player type=SpriteRenderer member=Color value='{""r"":255,""g"":0,""b"":0,""a"":255}'
  voltage scripts.compile reloadScene=true
  voltage screenshot scale=0.5
  voltage input.click x=40 y=12
  voltage hotkey.press id=Global.SaveScene
  voltage build.run gateway=true && voltage --game screenshot");
	}
}
