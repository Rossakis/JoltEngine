using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using ImGuiNET;
using Voltage.Editor.DebugUtils;
using Voltage.Editor.ImGuiCore;
using Voltage.Editor.ProjectFile;

namespace Voltage.Editor.Plugins
{
	/// <summary>Loads editor plugins, gates them on EditorPluginApi.Version, initializes each in isolation, and owns the window/menu registries. Assemblies cannot unload, so updating one needs an editor restart.</summary>
	public static class EditorPluginHost
	{
		private class ActivePlugin
		{
			public IEditorPlugin Instance;
			public PluginInstance Owner;
		}

		private class PluginMenuItem
		{
			public string[] PathSegments;
			public Action OnClick;
			public string OwnerId;
			public EditorMenu Menu = EditorMenu.Plugins;
		}

		private static readonly List<ActivePlugin> _active = new();
		private static readonly List<EditorPluginWindow> _windows = new();
		private static readonly List<PluginMenuItem> _menuItems = new();
		private static readonly HashSet<string> _loadedAssemblyPaths = new(StringComparer.OrdinalIgnoreCase);
		private static PluginContext _context;

		public static bool HasMenuItems => _menuItems.Count > 0;

		/// <summary>Loads and initializes the editor side of every restored plugin of kind "editor".</summary>
		public static void InitializePlugins(IReadOnlyList<PluginInstance> plugins, IGameProject project)
		{
			_context ??= new PluginContext();
			_context.Project = project;

			foreach (var plugin in plugins)
			{
				if (plugin.Manifest is not { IsEditor: true })
					continue;
				if (plugin.State is not (PluginState.Restored or PluginState.Loaded))
					continue;

				if (plugin.Manifest.EditorPluginApiVersion != EditorPluginApi.Version)
				{
					plugin.State = PluginState.Failed;
					plugin.Error = $"Editor plugin was built for editor-plugin API v{plugin.Manifest.EditorPluginApiVersion}, " +
						$"but this editor provides v{EditorPluginApi.Version}. Update the plugin (or the editor).";
					PluginLog.Error($"Plugin '{plugin.Id}': {plugin.Error}");
					continue;
				}

				try
				{
					InitializePluginAssemblies(plugin);
					plugin.State = PluginState.Loaded;
				}
				catch (Exception ex)
				{
					plugin.State = PluginState.Failed;
					plugin.Error = $"Editor plugin failed to initialize: {ex.Message}";
					PluginLog.Error($"Plugin '{plugin.Id}': {plugin.Error}");
				}
			}
		}

		private static void InitializePluginAssemblies(PluginInstance plugin)
		{
			foreach (var rel in plugin.Manifest.Editor.Assemblies)
			{
				var dllPath = Path.Combine(plugin.PayloadPath, PluginManifest.NormalizeRelative(rel));
				if (!File.Exists(dllPath))
					throw new FileNotFoundException($"Editor assembly not found in payload: {rel}", dllPath);

				var assembly = Assembly.LoadFrom(dllPath);
				_loadedAssemblyPaths.Add(dllPath);

				// LoadFrom resolves by identity, not path: an already-loaded assembly comes back
				// instead of dllPath, so the new build silently contributes nothing. Say so.
				if (!string.IsNullOrEmpty(assembly.Location) && !SamePath(assembly.Location, dllPath))
				{
					plugin.StaleAssemblyWarning =
						$"'{Path.GetFileName(dllPath)}' was already loaded in this session from " +
						$"{assembly.Location}, so the code running is still the previous build. .NET cannot " +
						"unload an assembly - restart the editor to pick up this one.";

					PluginLog.Warn($"Plugin '{plugin.Id}': {plugin.StaleAssemblyWarning}");
				}

				var pluginTypes = assembly.GetTypes()
					.Where(t => typeof(IEditorPlugin).IsAssignableFrom(t)
					            && t is { IsAbstract: false, IsInterface: false }
					            && t.GetConstructor(Type.EmptyTypes) != null)
					.ToList();

				if (pluginTypes.Count == 0)
				{
					PluginLog.Warn($"Plugin '{plugin.Id}': no IEditorPlugin implementation found in {Path.GetFileName(dllPath)}.");
					continue;
				}

				foreach (var type in pluginTypes)
				{
					// Instances survive project switches, so do not double-initialize.
					if (_active.Any(a => a.Instance.GetType() == type))
					{
						if (plugin.StaleAssemblyWarning == null)
						{
							plugin.StaleAssemblyWarning =
								$"'{type.Name}' is already running from an earlier load in this session, so it " +
								"was not initialized again and any new windows or menu items it registers are " +
								"absent. Restart the editor.";

							PluginLog.Warn($"Plugin '{plugin.Id}': {plugin.StaleAssemblyWarning}");
						}

						continue;
					}

					var instance = (IEditorPlugin)Activator.CreateInstance(type);
					_context.CurrentOwnerId = plugin.Id;
					instance.Initialize(_context);
					_context.CurrentOwnerId = null;

					_active.Add(new ActivePlugin { Instance = instance, Owner = plugin });
					PluginLog.Log($"Initialized editor plugin: {type.FullName} ({plugin.Id})");
				}
			}
		}

		private static bool SamePath(string a, string b)
		{
			try
			{
				return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
			}
			catch
			{
				return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
			}
		}

		/// <summary>Shuts every active editor plugin down (project close / editor exit).</summary>
		public static void ShutdownPlugins()
		{
			_context?.FireProjectClosing();

			foreach (var active in _active)
			{
				try
				{
					active.Instance.Shutdown();
				}
				catch (Exception ex)
				{
					PluginLog.Warn($"Editor plugin {active.Instance.GetType().Name} threw during Shutdown: {ex.Message}");
				}
			}

			_active.Clear();
			_windows.Clear();
			_menuItems.Clear();
			if (_context != null)
				_context.Project = null;
		}

		/// <summary>Draws all open plugin windows. Called once per frame from ImGuiManager.LayoutGui.</summary>
		public static void DrawWindows()
		{
			foreach (var window in _windows)
			{
				if (!window.IsOpen)
					continue;

				try
				{
					window.Draw();
				}
				catch (Exception ex)
				{
					window.IsOpen = false;
					PluginLog.Error($"Plugin window '{window.Title}' threw during Draw and was closed: {ex.Message}");
				}
			}
		}

		/// <summary>Draws the plugin-registered entries of the Plugins menu (supports "A/B/C" nesting).</summary>
		public static void DrawMenuItems() => DrawMenuItems(EditorMenu.Plugins);

		/// <summary>Draws plugin contributions for one menu, at the end so they sit below the host's own commands.</summary>
		public static void DrawMenuItems(EditorMenu menu)
		{
			var items = _menuItems.Where(i => i.Menu == menu).ToList();
			if (items.Count == 0)
				return;

			ImGui.Separator();
			DrawMenuLevel(items, 0);
		}

		private static void DrawMenuLevel(List<PluginMenuItem> items, int depth)
		{
			foreach (var item in items.Where(i => i.PathSegments.Length == depth + 1))
			{
				if (Gui.MenuItem(item.PathSegments[depth]))
				{
					try
					{
						item.OnClick?.Invoke();
					}
					catch (Exception ex)
					{
						PluginLog.Error($"Plugin menu item '{string.Join("/", item.PathSegments)}' threw: {ex.Message}");
					}
				}
			}

			foreach (var group in items.Where(i => i.PathSegments.Length > depth + 1)
				         .GroupBy(i => i.PathSegments[depth], StringComparer.Ordinal))
			{
				if (Gui.BeginMenu(group.Key))
				{
					DrawMenuLevel(group.ToList(), depth + 1);
					Gui.EndMenu();
				}
			}
		}

		#region Context implementation

		private class PluginContext : IEditorPluginContext
		{
			public IGameProject Project;

			/// <summary>Plugin id being initialized right now - stamps ownership onto registrations.</summary>
			public string CurrentOwnerId;

			public ImGuiManager ImGuiManager => Core.GetGlobalManager<ImGuiManager>();
			public IGameProject CurrentProject => Project;

			public event Action ProjectClosing;

			public void RegisterWindow(EditorPluginWindow window)
			{
				if (window == null)
					throw new ArgumentNullException(nameof(window));
				if (!_windows.Contains(window))
					_windows.Add(window);
			}

			public void AddMenuItem(string path, Action onClick) =>
				AddMenuItem(EditorMenu.Plugins, path, onClick);

			public void AddMenuItem(EditorMenu menu, string path, Action onClick)
			{
				if (string.IsNullOrWhiteSpace(path))
					throw new ArgumentException("Menu path cannot be empty.", nameof(path));

				_menuItems.Add(new PluginMenuItem
				{
					PathSegments = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
					OnClick = onClick,
					OwnerId = CurrentOwnerId,
					Menu = menu,
				});
			}

			public void FireProjectClosing()
			{
				try
				{
					ProjectClosing?.Invoke();
				}
				catch (Exception ex)
				{
					PluginLog.Warn($"A plugin ProjectClosing handler threw: {ex.Message}");
				}
			}
		}

		#endregion
	}
}
