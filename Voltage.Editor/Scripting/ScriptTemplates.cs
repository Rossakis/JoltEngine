using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Voltage.Editor.Scripting;

/// <summary>Starting points for new script files, shaped the way the engine's generators expect them.</summary>
public static class ScriptTemplates
{
	public static readonly IReadOnlyList<(string Id, string Label, string DefaultName)> Kinds = new[]
	{
		("component", "Component", "NewComponent"),
		("scenecomponent", "Scene Component", "NewSceneComponent"),
		("dataasset", "Data Asset", "NewDataAsset"),
		("empty", "Empty Class", "NewClass"),
	};

	public static bool IsKind(string id) => Kinds.Any(k => k.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

	/// <summary>The project's root script namespace, with folder segments appended for a subfolder.</summary>
	public static string Namespace(string projectName, IEnumerable<string> folderSegments = null)
	{
		var parts = new List<string> { Identifier(projectName), "Scripts" };
		if (folderSegments != null)
			parts.AddRange(folderSegments.Where(s => !string.IsNullOrWhiteSpace(s)).Select(Identifier));
		return string.Join(".", parts);
	}

	/// <summary>A C# identifier derived from any name: invalid characters dropped, a leading digit prefixed.</summary>
	public static string Identifier(string name)
	{
		var sb = new StringBuilder();
		foreach (var c in name ?? "")
			if (char.IsLetterOrDigit(c) || c == '_')
				sb.Append(c);
		if (sb.Length == 0)
			sb.Append("Script");
		if (char.IsDigit(sb[0]))
			sb.Insert(0, '_');
		return sb.ToString();
	}

	public static bool IsIdentifier(string name) =>
		!string.IsNullOrEmpty(name) && Identifier(name) == name;

	public static string Render(string kind, string className, string ns)
	{
		switch ((kind ?? "").ToLowerInvariant())
		{
			case "component":
				return $@"using Microsoft.Xna.Framework;
using Voltage;
using Voltage.Utils;

namespace {ns}
{{
	public partial class {className} : Component, IUpdatable
	{{
		public float Speed = 100f;

		public override void OnStart()
		{{
			base.OnStart();
		}}

		public void Update()
		{{
		}}
	}}
}}
";
			case "scenecomponent":
				return $@"using Microsoft.Xna.Framework;
using Voltage;
using Voltage.Utils;

namespace {ns}
{{
	public partial class {className} : SceneComponent
	{{
		public override void OnStart()
		{{
			base.OnStart();
		}}

		public override void Update()
		{{
			base.Update();
		}}
	}}
}}
";
			case "dataasset":
				return $@"using Microsoft.Xna.Framework;
using Voltage;
using Voltage.Data;

namespace {ns}
{{
	[AssetTypeId(""{className}"")]
	public partial class {className} : DataAsset
	{{
		public float Value = 1f;
	}}
}}
";
			case "empty":
				return $@"using System;
using Microsoft.Xna.Framework;
using Voltage;

namespace {ns}
{{
	public class {className}
	{{
	}}
}}
";
			default:
				throw new ArgumentException($"unknown script template '{kind}'; use {string.Join("|", Kinds.Select(k => k.Id))}");
		}
	}
}
