using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Voltage.Gateway;
using Voltage.Gateway.Commands;
using static Voltage.Gateway.InputSimulator;

namespace Voltage.Editor.Gateway.Commands;

/// <summary>Finds and clicks editor widgets by label, using the registry <see cref="ImGuiCore.Gui"/> fills each frame.</summary>
internal static class UiCommands
{
	private static readonly List<PathWalk> _walks = new();

	/// <summary>Clicks one menu level per step; the next level is looked up after the menu has had frames to open.</summary>
	private sealed class PathWalk
	{
		public GatewayContext Ctx;
		public string[] Levels;
		public int Level;
		public int FramesWaited;
		public Button Button;
		public Task<object> Pending;
		public readonly TaskCompletionSource<object> Done = new();
		public readonly List<object> Clicked = new();
	}

	private static readonly GatewayParam[] Target =
	{
		P.Str("label", "visible text to match, case-insensitive substring unless exact"),
		P.Str("path", "menu path such as File/Save Scene; opens each level"),
		P.Enum("kind", "widget kind", new[] { "button", "menu", "menuItem", "selectable", "checkbox", "radio", "tab", "tree", "header", "input", "drag", "slider", "combo", "color" }),
		P.Str("window", "restrict to a window by display name or id"),
		P.Bool("exact", "match the whole label", false),
		P.Int("index", "which match when several", 0)
	};

	public static void Register(GatewayCommandTable table)
	{
		table.Add("ui.windows", "Editor windows, popups and modals drawn last frame, with rects in window pixels.", (_, _) =>
			UiRegistry.Windows.Select(Describe).ToList()).ReadOnly();

		table.Add("ui.popups", "Open popups and modals with the buttons that answer them; a modal blocks every other click until one is pressed.", (_, _) =>
			UiRegistry.Windows.Where(w => w.Kind is "modal" or "popup").Select(w => new
			{
				kind = w.Kind,
				name = w.Display,
				id = w.Id,
				x = (int)w.X,
				y = (int)w.Y,
				width = (int)w.W,
				height = (int)w.H,
				buttons = UiRegistry.Items.Where(i => i.Kind == "button" && i.Visible && i.Window == w.Display).Select(i => i.Display).ToList()
			}).ToList()).ReadOnly();

		table.Add("ui.find", "Widgets drawn last frame whose label matches; exact matches first, then top to bottom.", (args, _) =>
		{
			var matches = Find(args, args.Bool("visibleOnly", true));
			return matches.Select((m, i) => Describe(m, i)).ToList();
		}, P.Str("label", "case-insensitive substring unless exact", required: true), Target[2], Target[3], Target[4], P.Bool("visibleOnly", "skip clipped widgets", true)).ReadOnly();

		table.Add("ui.tree", "Everything drawn last frame, grouped by window in draw order, so an agent can see what is on screen without a screenshot.", (args, _) =>
		{
			var window = args.String("window");
			var visibleOnly = args.Bool("visibleOnly", true);
			var limit = Math.Max(1, args.Int("limit", 500));
			var wanted = WindowSet(window);
			var items = UiRegistry.Items.Where(i => (!visibleOnly || i.Visible) && InWindow(i, wanted));
			var groups = new List<object>();
			var total = 0;
			foreach (var group in items.GroupBy(i => i.Window))
			{
				var rows = new List<object>();
				foreach (var item in group)
				{
					if (total++ >= limit)
						break;
					rows.Add(new { kind = item.Kind, label = item.Display, path = item.Path.Length == 0 ? null : item.Path, x = (int)item.X, y = (int)item.Y, w = (int)item.W, h = (int)item.H, enabled = item.Enabled ? (bool?)null : false });
				}
				groups.Add(new { window = group.Key, items = rows });
			}
			return new { windows = groups, truncated = total > limit };
		}, Target[3], P.Bool("visibleOnly", "skip clipped widgets", true), P.Int("limit", "maximum rows", 500)).ReadOnly();

		table.Add("ui.click", "Click a widget by label or a menu item by path. Prefer this over input.click coordinates.", (args, ctx) =>
		{
			var button = CoreCommands.ParseButton(args.String("button"));
			var path = args.String("path");
			if (!string.IsNullOrWhiteSpace(path))
				return WalkPath(ctx, path, button);

			var item = Pick(args);
			var (x, y) = Center(item);
			return ctx.Dispatcher.Input.Enqueue(Click(x, y, button, args.Int("count", 1)));
		}, Target.Concat(new[] { P.Enum("button", "mouse button", new[] { "left", "right", "middle" }, "left"), P.Int("count", "clicks", 1) }).ToArray()).Unsafe();

		table.Add("ui.hover", "Move the cursor onto a widget and wait, so its tooltip appears in the next screenshot.", (args, ctx) =>
		{
			var item = Pick(args);
			var (x, y) = Center(item);
			return ctx.Dispatcher.Input.Enqueue(new Step[] { new MoveStep(x, y), new WaitStep(Math.Max(1, args.Int("frames", 10))) });
		}, Target.Where(p => p.Name != "path").Append(P.Int("frames", "frames to hold", 10)).ToArray()).Unsafe();

		table.Add("ui.focus", "Bring an editor window to the front; a hidden window must be shown with window.show first.", (args, _) =>
		{
			var name = args.Require("window");
			var window = UiRegistry.Windows.FirstOrDefault(w => WindowMatches(w, name))
				?? throw new GatewayException($"no window '{name}' was drawn last frame; see ui.windows and window.list");
			UiRegistry.RequestFocus(window.Name);
			return Describe(window);
		}, P.Str("window", "display name or id", required: true));
	}

	/// <summary>Advances menu-path clicks; called every frame by the editor dispatcher.</summary>
	public static void Tick()
	{
		for (var i = _walks.Count - 1; i >= 0; i--)
		{
			var walk = _walks[i];
			if (walk.Pending != null)
			{
				if (!walk.Pending.IsCompleted)
					continue;
				if (walk.Pending.IsFaulted)
				{
					walk.Done.TrySetException(walk.Pending.Exception.GetBaseException());
					_walks.RemoveAt(i);
					continue;
				}
				walk.Pending = null;
				walk.Level++;
				walk.FramesWaited = 0;
				if (walk.Level >= walk.Levels.Length)
				{
					walk.Done.TrySetResult(new { path = string.Join("/", walk.Levels), clicked = walk.Clicked });
					_walks.RemoveAt(i);
					continue;
				}
			}

			// A level whose submenu is already open must not be clicked again: that would toggle it shut.
			while (walk.Level < walk.Levels.Length - 1 && SubmenuOpen(string.Join("/", walk.Levels.Take(walk.Level + 1))))
				walk.Level++;

			var item = FindLevel(walk);
			if (item == null)
			{
				if (++walk.FramesWaited < 60)
					continue;
				var prefix = string.Join("/", walk.Levels.Take(walk.Level));
				walk.Done.TrySetException(new GatewayException($"menu item '{walk.Levels[walk.Level]}' did not appear under '{prefix}'; {Suggest(walk.Levels[walk.Level], prefix.Length == 0 ? "MainMenuBar" : null)}"));
				_walks.RemoveAt(i);
				continue;
			}

			var (x, y) = Center(item);
			walk.Clicked.Add(Describe(item, walk.Level));
			walk.Pending = walk.Ctx.Dispatcher.Input.Enqueue(Click(x, y, walk.Button, 1).Concat(new Step[] { new WaitStep(3) }).ToList());
		}
	}

	private static object WalkPath(GatewayContext ctx, string path, Button button)
	{
		var levels = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (levels.Length == 0)
			throw new GatewayException("path is empty");
		var walk = new PathWalk { Ctx = ctx, Levels = levels, Button = button };
		_walks.Add(walk);
		return walk.Done.Task;
	}

	private static bool SubmenuOpen(string path)
	{
		foreach (var item in UiRegistry.Items)
			if (item.Visible && string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase))
				return true;
		return false;
	}

	/// <summary>Top level is a menu on the main menu bar; deeper levels sit under the path clicked so far.</summary>
	private static UiItem FindLevel(PathWalk walk)
	{
		var wanted = walk.Levels[walk.Level];
		var parent = string.Join("/", walk.Levels.Take(walk.Level));
		UiItem best = null;
		foreach (var item in UiRegistry.Items)
		{
			if (!item.Visible || (item.Kind != "menu" && item.Kind != "menuItem"))
				continue;
			if (!string.Equals(item.Path, parent, StringComparison.OrdinalIgnoreCase))
				continue;
			if (string.Equals(item.Display, wanted, StringComparison.OrdinalIgnoreCase))
				return item;
			if (best == null && item.Display.Contains(wanted, StringComparison.OrdinalIgnoreCase))
				best = item;
		}
		return best;
	}

	private static UiItem Pick(GatewayArgs args)
	{
		var matches = Find(args, true);
		var index = args.Int("index", 0);
		if (matches.Count == 0)
			throw new GatewayException($"no visible widget matches '{args.String("label")}'{Where(args)}; {Suggest(args.String("label"), args.String("window"))}");
		if (index < 0 || index >= matches.Count)
			throw new GatewayException($"index {index} is out of range; {matches.Count} widgets match '{args.String("label")}'");
		return matches[index];
	}

	private static List<UiItem> Find(GatewayArgs args, bool visibleOnly)
	{
		var label = args.String("label");
		if (string.IsNullOrWhiteSpace(label))
			throw new GatewayException("missing parameter 'label' (or 'path' for menus)");
		var kind = args.String("kind");
		var wanted = WindowSet(args.String("window"));
		var exact = args.Bool("exact", false);

		var exactMatches = new List<UiItem>();
		var partial = new List<UiItem>();
		foreach (var item in UiRegistry.Items)
		{
			if (visibleOnly && !item.Visible)
				continue;
			if (kind != null && !string.Equals(item.Kind, kind, StringComparison.OrdinalIgnoreCase))
				continue;
			if (!InWindow(item, wanted))
				continue;
			if (string.Equals(item.Display, label, StringComparison.OrdinalIgnoreCase))
				exactMatches.Add(item);
			else if (!exact && item.Display.Contains(label, StringComparison.OrdinalIgnoreCase))
				partial.Add(item);
		}

		exactMatches.Sort(TopToBottom);
		partial.Sort(TopToBottom);
		exactMatches.AddRange(partial);
		return exactMatches;
	}

	private static int TopToBottom(UiItem a, UiItem b)
	{
		var dy = a.Y.CompareTo(b.Y);
		return dy != 0 ? dy : a.X.CompareTo(b.X);
	}

	private static string Suggest(string label, string window)
	{
		var head = label == null || label.Length < 3 ? label : label.Substring(0, 3);
		var wanted = WindowSet(window);
		var near = UiRegistry.Items
			.Where(i => i.Visible && i.Display.Length > 0 && InWindow(i, wanted) && !string.IsNullOrEmpty(head) && i.Display.Contains(head, StringComparison.OrdinalIgnoreCase))
			.Select(i => i.Display).Distinct().Take(8).ToList();
		return near.Count > 0 ? $"similar labels: {string.Join(", ", near)}" : "use ui.tree to list what is on screen";
	}

	private static string Where(GatewayArgs args)
	{
		var parts = new List<string>();
		if (args.Has("kind")) parts.Add($"kind {args.String("kind")}");
		if (args.Has("window")) parts.Add($"window {args.String("window")}");
		return parts.Count == 0 ? "" : $" ({string.Join(", ", parts)})";
	}

	/// <summary>Display names of the windows a filter selects, resolved once per call; null means every window.</summary>
	private static HashSet<string> WindowSet(string wanted)
	{
		if (wanted == null)
			return null;
		var set = new HashSet<string>(StringComparer.Ordinal);
		foreach (var window in UiRegistry.Windows)
			if (WindowMatches(window, wanted))
				set.Add(window.Display);
		return set;
	}

	private static bool InWindow(UiItem item, HashSet<string> wanted) => wanted == null || wanted.Contains(item.Window);

	private static bool WindowMatches(UiWindow window, string wanted) =>
		string.Equals(window.Display, wanted, StringComparison.OrdinalIgnoreCase)
		|| string.Equals(window.Id, wanted, StringComparison.OrdinalIgnoreCase)
		|| string.Equals(window.Name, wanted, StringComparison.Ordinal)
		|| window.Display.Contains(wanted, StringComparison.OrdinalIgnoreCase);

	private static (int x, int y) Center(UiItem item) => ((int)MathF.Round(item.X + item.W / 2f), (int)MathF.Round(item.Y + item.H / 2f));

	private static object Describe(UiItem item, int index) => new
	{
		index,
		kind = item.Kind,
		label = item.Display,
		id = item.Label == item.Display ? null : item.Label,
		window = item.Window,
		path = item.Path.Length == 0 ? null : item.Path,
		x = (int)item.X,
		y = (int)item.Y,
		width = (int)item.W,
		height = (int)item.H,
		enabled = item.Enabled,
		hovered = item.Hovered,
		active = item.Active
	};

	private static object Describe(UiWindow w) => new
	{
		kind = w.Kind,
		name = w.Display,
		id = w.Id,
		x = (int)w.X,
		y = (int)w.Y,
		width = (int)w.W,
		height = (int)w.H,
		open = w.Open,
		modal = w.Kind == "modal",
		focused = w.Focused,
		collapsed = w.Collapsed,
		docked = w.Docked,
		items = w.ItemCount
	};
}
