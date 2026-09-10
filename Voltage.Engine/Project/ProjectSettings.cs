using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Voltage.Project
{
	[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
	public class ProjectSettings
	{

		public static ProjectSettings Instance
		{
			get
			{
				if (_instance == null)
					_instance = new ProjectSettings();
				return _instance;
			}
			set
			{
				_instance = value;
			}
		}

		private static ProjectSettings _instance;

		/// <summary>
		/// The .vscene file name (without extension) that the game loads on startup.
		/// When empty, the game creates a default empty scene (called MainScene).
		/// </summary>
		public string InitialScene;
		public DisplaySettings Display;
		public AudioSettings Audio;
		public DesignResolutionSettings DesignResolution;
		public PhysicsSettings Physics;
		public RenderingSettings Rendering;
		public EntitySettings Entities;
		public string ContentDirectory;
		public AssetBuildSettings AssetBuild;

		[DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(ProjectSettings))]
		[DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(DisplaySettings))]
		[DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(AudioSettings))]
		[DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(DesignResolutionSettings))]
		[DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(PhysicsSettings))]
		[DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(RenderingSettings))]
		[DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(EntitySettings))]
		[DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(AssetBuildSettings))]
		[DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(string))]
		public ProjectSettings()
		{
			InitialScene = "";
			Display = new DisplaySettings();
			Audio = new AudioSettings();
			DesignResolution = new DesignResolutionSettings();
			Physics = new PhysicsSettings();
			Rendering = new RenderingSettings();
			Entities = new EntitySettings();
			ContentDirectory = "Content";
			AssetBuild = new AssetBuildSettings();
		}

		/// <summary>How a game build compiles the Content folder with MGCB; off by default and ignored by the editor.</summary>
		[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
		public class AssetBuildSettings
		{
			public bool Enabled = false;
			public string Platform = "DesktopGL";
			public bool Compress = true;
			public bool PremultiplyAlpha = false;
			public string TextureFormat = "Color";
			public bool CompileAudio = false;

			[DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(List<string>))]
			public List<string> Include = new() { "**/*" };

			[DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(List<string>))]
			public List<string> Exclude = new() { "**/Voltage/**", "**/*.fx", "**/*.mgfxo" };

			[DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(List<AssetBuildRule>))]
			[DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(AssetBuildRule))]
			public List<AssetBuildRule> Rules = new();
		}

		/// <summary>Routes files with the listed extensions through an importer and processor, or copies or skips them; project rules override the built-in table.</summary>
		[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
		public class AssetBuildRule
		{
			public string Name = "";

			[DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(List<string>))]
			public List<string> Extensions = new();

			public string Action = "compile";
			public string Importer = "";
			public string Processor = "";

			[DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(List<string>))]
			public List<string> Parameters = new();

			public string Assembly = "";
		}

		[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
		public class DisplaySettings
		{
			public int ScreenWidth = 1280;
			public int ScreenHeight = 720;
			public bool IsFullscreen = false;
			public bool EnableVSync = true;
		}

		[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
		public class AudioSettings
		{
			public float MasterVolume = 1.0f;
			public float MusicVolume = 0.8f;
			public float SFXVolume = 1.0f;
		}

		[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
		public class DesignResolutionSettings
		{
			public int Width = 1280;
			public int Height = 720;
			public Scene.SceneResolutionPolicy ResolutionPolicy = Scene.SceneResolutionPolicy.BestFit;
			public int HorizontalBleed = 0;
			public int VerticalBleed = 0;
		}

		[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
		public class PhysicsSettings
		{
			[DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(Dictionary<string, int>))]
			public Dictionary<string, int> PhysicsLayers = new()
			{
				{ "Default", 0 },
				{ "Ground", 1 }
			};
		}

		[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
		public class RenderingSettings
		{
			public Microsoft.Xna.Framework.Color BackgroundClearColor =
				new Microsoft.Xna.Framework.Color(100, 149, 237, 255);

			[DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(Dictionary<string, int>))]
			public Dictionary<string, int> RenderingLayers = new()
			{
				{ "Lighting", 100 },
				{ "BehindAll", 99 },
				{ "HideObject", 30 },
				{ "Background", 0 },
				{ "Entities", 1 },
				{ "Foreground", -2 },
				{ "InFrontOfAll", -30 },
				{ "UIElement", -99 }
			};
		}

		[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
		public class EntitySettings
		{
			[DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(Dictionary<string, int>))]
			public Dictionary<string, int> EntityTags = new()
			{
				{ "Default", 0 },
				{ "Player", 1 }
			};
		}
	}
}
