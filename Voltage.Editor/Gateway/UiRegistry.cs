using System;
using System.Collections;
using System.Collections.Generic;
using ImGuiNET;

namespace Voltage.Editor.Gateway;

/// <summary>One widget drawn last frame, in window pixels.</summary>
internal sealed class UiItem
{
	public string Kind;
	public string Label;
	public string Display;
	public string Window;
	public string Path;
	public float X, Y, W, H;
	public bool Visible, Enabled, Hovered, Active;
}

/// <summary>One window, popup or modal begun last frame.</summary>
internal sealed class UiWindow
{
	public string Kind;
	public string Name;
	public string Display;
	public string Id;
	public float X, Y, W, H;
	public bool Focused, Collapsed, Docked, Open;
	public int ItemCount;
}

/// <summary>Per-frame record of what <see cref="ImGuiCore.Gui"/> drew, so the gateway can aim by label instead of pixels. Commands run before layout, so they read the previous frame.</summary>
internal static class UiRegistry
{
	/// <summary>A pooled list that hands out reused objects; only the first <see cref="Count"/> are live.</summary>
	private sealed class Frame<T> : IReadOnlyList<T> where T : class, new()
	{
		private readonly List<T> _pool;

		public Frame(int capacity) => _pool = new List<T>(capacity);

		public int Count { get; private set; }

		public T this[int index] => index < Count ? _pool[index] : throw new ArgumentOutOfRangeException(nameof(index));

		public T Next()
		{
			if (Count == _pool.Count)
				_pool.Add(new T());
			return _pool[Count++];
		}

		public void Reset() => Count = 0;

		public IEnumerator<T> GetEnumerator()
		{
			for (var i = 0; i < Count; i++)
				yield return _pool[i];
		}

		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	}

	private const int DisplayCacheLimit = 4096;

	private static Frame<UiItem> _items = new(512);
	private static Frame<UiItem> _lastItems = new(512);
	private static Frame<UiWindow> _windows = new(64);
	private static Frame<UiWindow> _lastWindows = new(64);
	private static readonly List<UiWindow> _windowStack = new(16);
	private static readonly List<string> _menuStack = new(8);
	private static readonly Dictionary<string, string> _displayCache = new(StringComparer.Ordinal);
	private static string _menuPath = "";
	private static string _pendingFocus;

	public static IReadOnlyList<UiItem> Items => _lastItems;

	public static IReadOnlyList<UiWindow> Windows => _lastWindows;

	/// <summary>Raw label of the last widget recorded this frame.</summary>
	public static string LastLabel { get; private set; }

	/// <summary>Call once per frame before ImGui.NewFrame.</summary>
	public static void BeginFrame()
	{
		(_lastItems, _items) = (_items, _lastItems);
		(_lastWindows, _windows) = (_windows, _lastWindows);
		_items.Reset();
		_windows.Reset();
		_windowStack.Clear();
		_menuStack.Clear();
		_menuPath = "";
		LastLabel = null;
	}

	/// <summary>Focuses the window with this raw name the next time it begins.</summary>
	public static void RequestFocus(string rawName) => _pendingFocus = rawName;

	internal static void PushWindow(string kind, string name, bool open)
	{
		var window = _windows.Next();
		window.Kind = kind;
		window.Name = name;
		window.Display = DisplayOf(name);
		window.Id = IdOf(name);
		window.Open = open;
		window.ItemCount = 0;
		window.X = window.Y = window.W = window.H = 0f;
		window.Focused = window.Collapsed = window.Docked = false;
		if (ImGui.GetCurrentContext() != IntPtr.Zero)
		{
			var pos = ImGui.GetWindowPos();
			var size = ImGui.GetWindowSize();
			window.X = pos.X;
			window.Y = pos.Y;
			window.W = size.X;
			window.H = size.Y;
			window.Focused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
			window.Collapsed = ImGui.IsWindowCollapsed();
			window.Docked = ImGui.IsWindowDocked();
			if (_pendingFocus != null && string.Equals(_pendingFocus, name, StringComparison.Ordinal))
			{
				ImGui.SetWindowFocus();
				_pendingFocus = null;
			}
		}
		_windowStack.Add(window);
	}

	internal static void PopWindow(params string[] kinds)
	{
		var n = _windowStack.Count;
		if (n > 0 && Array.IndexOf(kinds, _windowStack[n - 1].Kind) >= 0)
			_windowStack.RemoveAt(n - 1);
	}

	internal static void PushMenu(string label)
	{
		_menuStack.Add(DisplayOf(label));
		_menuPath = string.Join("/", _menuStack);
	}

	internal static void PopMenu()
	{
		if (_menuStack.Count == 0)
			return;
		_menuStack.RemoveAt(_menuStack.Count - 1);
		_menuPath = _menuStack.Count == 0 ? "" : string.Join("/", _menuStack);
	}

	/// <summary>A name for an anonymous context popup: the item it hangs off, or the window it belongs to.</summary>
	internal static string ContextPopupName(bool forItem)
	{
		if (forItem)
		{
			var display = DisplayOf(LastLabel);
			return display.Length > 0 ? "context:" + display : "context#" + ImGui.GetItemID();
		}
		var window = _windowStack.Count > 0 ? _windowStack[_windowStack.Count - 1].Display : "";
		return window.Length > 0 ? "context:" + window : "context";
	}

	internal static void Record(string kind, string label, bool enabled = true)
	{
		if (ImGui.GetCurrentContext() == IntPtr.Zero)
			return;

		var min = ImGui.GetItemRectMin();
		var max = ImGui.GetItemRectMax();
		var window = _windowStack.Count > 0 ? _windowStack[_windowStack.Count - 1] : null;
		if (window != null)
			window.ItemCount++;

		LastLabel = label;
		var item = _items.Next();
		item.Kind = kind;
		item.Label = label ?? "";
		item.Display = DisplayOf(label);
		item.Window = window?.Display ?? "";
		item.Path = _menuPath;
		item.X = min.X;
		item.Y = min.Y;
		item.W = max.X - min.X;
		item.H = max.Y - min.Y;
		item.Visible = ImGui.IsItemVisible();
		item.Enabled = enabled;
		item.Hovered = ImGui.IsItemHovered();
		item.Active = ImGui.IsItemActive();
	}

	/// <summary>The text a user sees: everything before "##"; an id-only label falls back to the id.</summary>
	public static string DisplayOf(string label)
	{
		if (string.IsNullOrEmpty(label))
			return "";
		if (_displayCache.TryGetValue(label, out var cached))
			return cached;
		if (_displayCache.Count >= DisplayCacheLimit)
			_displayCache.Clear();

		var i = label.IndexOf("##", StringComparison.Ordinal);
		string display;
		if (i < 0)
			display = label.Trim();
		else
		{
			var visible = label.Substring(0, i).Trim();
			display = visible.Length > 0 ? visible : IdOf(label);
		}
		_displayCache[label] = display;
		return display;
	}

	/// <summary>The identifier part after "##" or "###", or the whole label.</summary>
	public static string IdOf(string label)
	{
		if (string.IsNullOrEmpty(label))
			return "";
		var i = label.LastIndexOf("##", StringComparison.Ordinal);
		if (i < 0)
			return label.Trim();
		var j = i + 2;
		if (j < label.Length && label[j] == '#')
			j++;
		return label.Substring(j).Trim();
	}
}
