using System;
using System.Collections.Generic;
using System.Linq;

namespace Voltage.Gateway;

/// <summary>Per-request context handed to a handler.</summary>
public class GatewayContext
{
	public GatewayClient Client { get; init; }

	public GatewayServer Server { get; init; }

	public GatewayCommandTable Commands { get; init; }

	public GatewayDispatcher Dispatcher { get; init; }
}

/// <summary>Handles one request on the main thread. Return a plain object, or a Task&lt;object&gt; to answer later.</summary>
public delegate object GatewayHandler(GatewayArgs args, GatewayContext ctx);

/// <summary>One declared parameter; Type is a JSON schema type name (string, integer, number, boolean, array, object, any).</summary>
public sealed record GatewayParam(string Name, string Type, string Description = null, object Default = null, bool Required = false, string[] Enum = null, string ItemType = null);

/// <summary>Shorthand constructors for <see cref="GatewayParam"/>.</summary>
public static class P
{
	public static GatewayParam Str(string name, string desc = null, string def = null, bool required = false) => new(name, "string", desc, def, required);

	public static GatewayParam Int(string name, string desc = null, int? def = null, bool required = false) => new(name, "integer", desc, def, required);

	public static GatewayParam Float(string name, string desc = null, float? def = null, bool required = false) => new(name, "number", desc, def, required);

	public static GatewayParam Bool(string name, string desc = null, bool? def = null, bool required = false) => new(name, "boolean", desc, def, required);

	public static GatewayParam Enum(string name, string desc, string[] values, string def = null, bool required = false) => new(name, "string", desc, def, required, values);

	public static GatewayParam List(string name, string desc = null, string itemType = "string", bool required = false) => new(name, "array", desc, null, required, null, itemType);

	public static GatewayParam Obj(string name, string desc = null, bool required = false) => new(name, "object", desc, null, required);

	public static GatewayParam Any(string name, string desc = null, bool required = false) => new(name, "any", desc, null, required);
}

public sealed class GatewayCommand
{
	public string Name { get; }

	public string Help { get; }

	public GatewayHandler Handler { get; }

	public IReadOnlyList<GatewayParam> Params { get; }

	/// <summary>Inspects state only.</summary>
	public bool IsReadOnly { get; private set; }

	/// <summary>Deletes or replaces user data irreversibly from the caller's point of view.</summary>
	public bool IsDestructive { get; private set; }

	/// <summary>Launches processes, exits, crashes or writes outside the project; refused under --gateway-safe.</summary>
	public bool IsUnsafe { get; private set; }

	public GatewayCommand(string name, string help, GatewayHandler handler, IReadOnlyList<GatewayParam> parameters)
	{
		Name = name;
		Help = help;
		Handler = handler;
		Params = parameters ?? Array.Empty<GatewayParam>();
	}

	public GatewayCommand ReadOnly()
	{
		IsReadOnly = true;
		return this;
	}

	public GatewayCommand Destructive()
	{
		IsDestructive = true;
		return this;
	}

	public GatewayCommand Unsafe()
	{
		IsUnsafe = true;
		return this;
	}

	/// <summary>The shape the CLI and MCP server read from the 'commands' method.</summary>
	public object Describe() => new
	{
		name = Name,
		help = Help,
		@params = Params.Select(p => new
		{
			name = p.Name,
			type = p.Type,
			description = p.Description,
			@default = p.Default,
			required = p.Required ? true : (bool?)null,
			@enum = p.Enum,
			itemType = p.ItemType
		}).ToList(),
		readOnly = IsReadOnly,
		destructive = IsDestructive,
		@unsafe = IsUnsafe
	};
}

/// <summary>Name-indexed registry of every gateway command. Adding an existing name replaces it.</summary>
public sealed class GatewayCommandTable
{
	private readonly Dictionary<string, GatewayCommand> _commands = new(StringComparer.OrdinalIgnoreCase);

	public IEnumerable<GatewayCommand> All => _commands.Values.OrderBy(c => c.Name, StringComparer.Ordinal);

	public GatewayCommand Add(string name, string help, GatewayHandler handler, params GatewayParam[] parameters)
	{
		var command = new GatewayCommand(name, help, handler, parameters);
		_commands[name] = command;
		return command;
	}

	public bool TryGet(string name, out GatewayCommand command) => _commands.TryGetValue(name ?? "", out command);
}
