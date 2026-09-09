using Voltage.Gateway;

namespace Voltage.Editor;

/// <summary>How this editor process was launched; read by UI code that must not block an unattended run.</summary>
public static class EditorRunMode
{
	/// <summary>Startup prompts log a warning and raise a prompt.suppressed event instead of opening.</summary>
	public static bool NoPrompts { get; private set; }

	/// <summary>Window hidden, loop never pauses; implies <see cref="NoPrompts"/>.</summary>
	public static bool Headless { get; private set; }

	public static void Apply(GatewayOptions options)
	{
		NoPrompts = options.NoPrompts || options.Headless;
		Headless = options.Headless;
	}
}
