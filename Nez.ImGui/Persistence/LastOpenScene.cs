using Nez.ImGuiTools.Persistence;
using System;

namespace Nez.ImGuiTools.Utils
{
	/// <summary>
	/// Tracks and persists the last opened scene across sessions using ImGuiSettingsLoader.
	/// </summary>
	public static class LastOpenScene
	{
		private const string SettingsKey = "LastOpenScene";
		private const string DefaultSceneName = "";

		/// <summary>
		/// Gets the fully qualified type name of the last opened scene.
		/// </summary>
		public static string GetLastSceneName()
		{
			return ImGuiSettingsLoader.LoadSetting(SettingsKey, DefaultSceneName);
		}

		/// <summary>
		/// Saves the current scene's type name.
		/// </summary>
		/// <param name="scene">The scene to remember</param>
		public static void SetLastScene(Scene scene)
		{
			if (scene == null)
				return;

			var sceneTypeName = scene.GetType().AssemblyQualifiedName;
			ImGuiSettingsLoader.SaveSetting(SettingsKey, sceneTypeName);
		}

		/// <summary>
		/// Attempts to create an instance of the last opened scene.
		/// </summary>
		/// <returns>A new instance of the last scene, or null if it cannot be created</returns>
		public static Scene CreateLastScene()
		{
			var sceneTypeName = GetLastSceneName();
			
			if (string.IsNullOrEmpty(sceneTypeName))
				return null;

			try
			{
				var sceneType = Type.GetType(sceneTypeName);
				
				if (sceneType == null || !typeof(Scene).IsAssignableFrom(sceneType))
					return null;

				return (Scene)Activator.CreateInstance(sceneType);
			}
			catch (Exception ex)
			{
				Debug.Log(Debug.LogType.Error, $"Failed to create last scene from type '{sceneTypeName}': {ex.Message}");
				return null;
			}
		}

		/// <summary>
		/// Clears the last scene setting.
		/// </summary>
		public static void Clear()
		{
			ImGuiSettingsLoader.SaveSetting(SettingsKey, DefaultSceneName);
		}
	}
}