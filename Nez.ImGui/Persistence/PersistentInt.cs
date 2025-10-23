using System;

namespace Nez.ImGuiTools.Persistence
{
	public class PersistentInt
	{
		public string Key { get; }
		private int _value;

		public PersistentInt(string key, int defaultValue = 0)
		{
			Key = key;
			_value = ImGuiSettingsLoader.LoadSetting(key, defaultValue);
		}

		public int Value
		{
			get => _value;
			set
			{
				if (_value != value)
				{
					_value = value;
					ImGuiSettingsLoader.SaveSetting(Key, _value);
				}
			}
		}
	}
}