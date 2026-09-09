using System;
using System.Linq;
using System.Reflection;
using Voltage.Editor.DebugUtils;

namespace Voltage.Editor.Utils;

/// <summary>
/// Resolves the MonoGame.Framework.DesktopGL package version at runtime from the
/// loaded assembly metadata. Shared by <c>ProjectStructureGenerator</c>,
/// <c>EngineLibsSync</c>, and any other code that needs the NuGet package version.
/// </summary>
public static class MonoGameVersionResolver
{
	private static string _cachedVersion;

	/// <summary>
	/// Returns the MonoGame.Framework.DesktopGL NuGet package version currently loaded
	/// by the editor. The result is cached after the first call.
	/// Falls back to a hardcoded default if detection fails.
	/// </summary>
	public static string GetVersion()
	{
		if (_cachedVersion != null)
			return _cachedVersion;

		try
		{
			var monoGameAssembly = AppDomain.CurrentDomain.GetAssemblies()
				.FirstOrDefault(a => a.GetName().Name == "MonoGame.Framework");

			if (monoGameAssembly != null)
			{
				// Try InformationalVersion first  NuGet packages set this to the full semver string
				var infoAttr = monoGameAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
				if (infoAttr != null && !string.IsNullOrWhiteSpace(infoAttr.InformationalVersion))
				{
					var version = infoAttr.InformationalVersion;
					var plusIndex = version.IndexOf('+');
					if (plusIndex >= 0)
						version = version[..plusIndex];

					EditorDebug.Log($"Detected MonoGame version from InformationalVersion: {version}", "MonoGameVersion");
					_cachedVersion = version;
					return _cachedVersion;
				}

				// Fall back to assembly version
				var assemblyVersion = monoGameAssembly.GetName().Version;
				if (assemblyVersion != null)
				{
					var version = $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}";
					EditorDebug.Log($"Detected MonoGame version from AssemblyVersion: {version}", "MonoGameVersion");
					_cachedVersion = version;
					return _cachedVersion;
				}
			}
		}
		catch (Exception ex)
		{
			EditorDebug.Warn($"Could not detect MonoGame version: {ex.Message}", "MonoGameVersion");
		}

		const string fallback = "3.8.5.1";
		EditorDebug.Warn($"Using fallback MonoGame version: {fallback}", "MonoGameVersion");
		_cachedVersion = fallback;
		return _cachedVersion;
	}
}