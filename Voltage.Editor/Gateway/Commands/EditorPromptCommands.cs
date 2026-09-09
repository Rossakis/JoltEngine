using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Voltage.Editor.Effects;
using Voltage.Editor.ImGuiCore;
using Voltage.Editor.Plugins;
using Voltage.Editor.ProjectFile;
using Voltage.Gateway;

namespace Voltage.Editor.Gateway.Commands;

/// <summary>What the startup prompts offer, as deliberate commands: plugins the project is missing and the engine effects build.</summary>
internal static class EditorPromptCommands
{
	private static readonly TimeSpan EffectsTimeout = TimeSpan.FromMinutes(10);
	private static readonly TimeSpan RestoreTimeout = TimeSpan.FromMinutes(15);

	public static void Register(GatewayCommandTable table)
	{
		table.Add("plugin.list", "Plugins the project declares, with state and, for unavailable ones, why: fetchable, unpublished, brokenLocal or missingInRepo.", (_, _) =>
		{
			if (ProjectManager.Instance?.HasActiveProject != true)
				throw new GatewayException("no project loaded");
			return (PluginManager.Instance?.Plugins ?? Array.Empty<PluginInstance>()).Select(p => new
			{
				id = p.Id,
				name = p.DisplayName,
				state = p.State.ToString(),
				source = p.Entry?.Source?.Describe(),
				localOverride = p.IsLocalOverride,
				problem = PluginRestorePrompt.Describe(p),
				error = p.Error
			}).ToList();
		}).ReadOnly();

		table.Add("plugin.restore", "Fetch the project's missing plugins the way the Plugins Needed prompt does; answers when the installs finish.", (args, _) =>
		{
			if (ProjectManager.Instance?.HasActiveProject != true)
				throw new GatewayException("no project loaded");
			if (PluginRestorePrompt.IsInstalling || PluginInstaller.IsBusy)
				throw new GatewayException("a plugin install is already running");

			PluginRestorePrompt.Update(PluginManager.Instance?.Plugins);
			var name = args.String("name");
			var queued = PluginRestorePrompt.Install(name);
			if (queued == 0)
				throw new GatewayException(name == null ? "nothing to fetch; see plugin.list" : $"'{name}' is not a fetchable missing plugin; see plugin.list");

			return GatewayTasks.WhenReady(() => !PluginRestorePrompt.IsInstalling && !PluginInstaller.IsBusy, () =>
			{
				if (PluginRestorePrompt.IsInstalling || PluginInstaller.IsBusy)
					throw new GatewayException("plugin restore did not finish in time");
				var jobs = PluginInstaller.Jobs.Select(j => new { id = j.PluginId, name = j.DisplayName, state = j.State.ToString(), message = j.Message }).ToList();
				return new { queued, jobs };
			}, (float)RestoreTimeout.TotalSeconds);
		}, P.Str("name", "one plugin id; every fetchable one when omitted")).Unsafe();

		table.Add("effects.status", "Whether compiled engine effects exist, where they are looked for, and whether the compiler is on PATH.", (_, _) =>
		{
			var dir = ImGuiManager.EngineEffectsDirectory;
			var count = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.mgfxo", SearchOption.AllDirectories).Length : 0;
			return new { directory = dir, compiled = count > 0, count, compilerAvailable = EffectsCompiler.IsMgfxcAvailable() };
		}).ReadOnly();

		table.Add("effects.compile", "Compile the engine effects, as the Missing Engine Effects prompt offers; answers with the counts when the build ends.", (_, ctx) =>
		{
			if (!EffectsCompiler.IsMgfxcAvailable())
				throw new GatewayException("mgfxc is not on PATH; install the MonoGame SDK tools");

			var done = new TaskCompletionSource<object>();
			Action<int, int> completed = null;
			completed = (ok, failed) =>
			{
				EffectsCompiler.OnBuildCompleted -= completed;
				done.TrySetResult(new { succeeded = ok, failed, directory = ImGuiManager.EngineEffectsDirectory });
			};
			EffectsCompiler.OnBuildCompleted += completed;
			ctx.ImGui().CompileEngineEffects();
			return GatewayTasks.WithTimeout(done, EffectsTimeout, () => EffectsCompiler.OnBuildCompleted -= completed, "the effects build did not finish in time");
		}).Unsafe();
	}
}
