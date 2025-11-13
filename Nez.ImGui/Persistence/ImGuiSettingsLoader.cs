using System;
using System.Collections.Generic;
using System.IO;
using Nez.Persistence;

namespace Nez.ImGuiTools.Persistence
{
	public static class ImGuiSettingsLoader
	{
		private static readonly string SettingsFilePath = GetSettingsFilePath();
		private static Dictionary<string, object> _settings;

		static ImGuiSettingsLoader()
		{
			LoadSettingsFromFile();
		}

		private static void LoadSettingsFromFile()
		{
			if (File.Exists(SettingsFilePath))
			{
				try
				{
					var json = File.ReadAllText(SettingsFilePath);
					_settings = Json.FromJson<Dictionary<string, object>>(json) ?? new Dictionary<string, object>();
				}
				catch
				{
					_settings = new Dictionary<string, object>();
				}
			}
			else
			{
				_settings = new Dictionary<string, object>();
			}
		}

		private static void SaveSettingsToFile()
		{
			try
			{
				var json = Json.ToJson(_settings);
				File.WriteAllText(SettingsFilePath, json);
			}
			catch
			{
				Debug.Error("Couldn't Save the Editor's Settings.");
			}
		}

		/// <summary>
		/// Save a setting value (e.g. ImGuiSettingsSaver.SaveSetting(_groupLogs, "GroupLogs"))
		/// </summary>
		public static void SaveSetting<T>(string key, T value)
		{
			_settings[key] = value;
			SaveSettingsToFile();
		}

		public static void SaveSetting(string key, bool value)
		{
			_settings[key] = value;
			SaveSettingsToFile();
		}

		public static void SaveSetting(string key, int value)
		{
			_settings[key] = value.ToString();
			SaveSettingsToFile();
		}

		public static void SaveSetting(string key, float value)
		{
			_settings[key] = value;
			SaveSettingsToFile();
		}

		public static void SaveSetting(string key, string value)
		{
			_settings[key] = value;
			SaveSettingsToFile();
		}

		/// <summary>
		/// Load a setting value (e.g. _groupLogs = ImGuiSettingsSaver.LoadSetting(_groupLogs, "GroupLogs"))
		/// </summary>
		public static bool LoadSetting(string key, bool defaultValue)
		{
			if (_settings != null && _settings.TryGetValue(key, out var val))
			{
				try
				{
					// Handle type conversion for bool
					if (val is bool b) return b;
					if (val is string s) return bool.Parse(s);
				}
				catch
				{
					return defaultValue;
				}
			}
			return defaultValue;
		}

		public static int LoadSetting(string key, int defaultValue)
		{
			if (_settings != null && _settings.TryGetValue(key, out var val))
			{
				try
				{
					// Handle type conversion for int
					if (val is int i) return i;
					if (val is string s) return int.Parse(s);
				}
				catch
				{
					return defaultValue;
				}
			}
			return defaultValue;
		}

		public static float LoadSetting(string key, float defaultValue)
		{
			if (_settings != null && _settings.TryGetValue(key, out var val))
			{
				try
				{
					// Handle type conversion for float
					if (val is float f) return f;
					if (val is string s) return float.Parse(s);
				}
				catch
				{
					return defaultValue;
				}
			}
			return defaultValue;
		}

		public static string LoadSetting(string key, string defaultValue)
		{
			if (_settings != null && _settings.TryGetValue(key, out var val))
			{
				try
				{
					// Handle type conversion for string
					if (val is string s) return s;
					return val?.ToString() ?? defaultValue;
				}
				catch
				{
					return defaultValue;
				}
			}
			return defaultValue;
		}

		private static string GetSettingsFilePath()
		{
			string appName = "JoltEngine";
			string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
			string settingsDir = Path.Combine(baseDir, appName);
			Directory.CreateDirectory(settingsDir);
			return Path.Combine(settingsDir, "ImGuiEditorSettings.json");
		}
	}
}