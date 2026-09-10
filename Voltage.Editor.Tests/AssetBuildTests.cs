using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using NUnit.Framework;
using Voltage.Cli;
using static Voltage.Editor.Tests.EditorSession;

namespace Voltage.Editor.Tests;

[TestFixture]
public class AssetBuildTests
{
	[Test]
	public void Settings_round_trip_through_project_settings()
	{
		var set = Call("assetbuild.settings", new { enabled = true, compileAudio = true, textureFormat = "Compressed" });
		Assert.That(set.GetProperty("saved").GetBoolean(), Is.True);

		var read = Call("assetbuild.settings");
		Assert.That(read.GetProperty("enabled").GetBoolean(), Is.True);
		Assert.That(read.GetProperty("compileAudio").GetBoolean(), Is.True);
		Assert.That(read.GetProperty("textureFormat").GetString(), Is.EqualTo("Compressed"));

		var file = Path.Combine(Path.GetDirectoryName(ProjectFile)!, "ProjectSettings.json");
		Assert.That(File.ReadAllText(file), Does.Contain("\"AssetBuild\""));

		Call("assetbuild.settings", new { enabled = false, compileAudio = false, textureFormat = "Color" });
		Assert.That(Call("assetbuild.settings").GetProperty("enabled").GetBoolean(), Is.False);
	}

	[Test]
	public void Run_compiles_an_imported_png_and_writes_the_index()
	{
		var png = Path.Combine(Path.GetDirectoryName(EditorExe)!, "DefaultContent", "Fonts", "VoltageDefaultBMFont.png");
		if (!File.Exists(png))
			Assert.Ignore($"sample texture not found at {png}");

		var imported = Call("asset.import", new { source = png, destination = "Textures", name = "Sample.png", overwrite = true });
		Assert.That(imported.GetProperty("path").GetString(), Does.EndWith("Sample.png"));

		JsonElement report;
		using (var slow = SlowConnection())
		{
			try
			{
				report = slow.Call("assetbuild.run", JsonSerializer.SerializeToElement(new { clean = true }));
			}
			catch (CliException ex) when (ex.Message.Contains("tool restore", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("NuGet", StringComparison.OrdinalIgnoreCase))
			{
				Assert.Ignore($"dotnet-mgcb could not be restored here: {ex.Message}");
				return;
			}
		}

		var items = report.GetProperty("items").EnumerateArray().ToList();
		var sample = items.FirstOrDefault(i => i.GetProperty("path").GetString()!.EndsWith("Textures/Sample.png", StringComparison.OrdinalIgnoreCase));
		Assert.That(sample.ValueKind, Is.EqualTo(JsonValueKind.Object), "the imported PNG is missing from the report");
		Assert.That(sample.GetProperty("outcome").GetString(), Is.EqualTo("compiled"), sample.TryGetProperty("error", out var err) ? err.ToString() : "");
		Assert.That(report.GetProperty("success").GetBoolean(), Is.True, string.Join("\n", report.GetProperty("errors").EnumerateArray().Select(e => e.ToString())));

		var index = report.GetProperty("index").GetString();
		Assert.That(File.Exists(index), Is.True, "content.index was not written");
		var line = File.ReadAllLines(index!).FirstOrDefault(l => l.EndsWith("\tTextures/Sample", StringComparison.Ordinal));
		Assert.That(line, Is.Not.Null, "content.index has no entry for the PNG");
		Assert.That(File.Exists(Path.Combine(report.GetProperty("output").GetString()!, "Textures", "Sample.xnb")), Is.True);

		var status = Call("assetbuild.status");
		Assert.That(status.GetProperty("restored").GetBoolean(), Is.True);
		Assert.That(status.GetProperty("indexEntries").GetInt32(), Is.GreaterThanOrEqualTo(1));
	}

	/// <summary>The first run downloads dotnet-mgcb, which can take minutes.</summary>
	private static GatewayConnection SlowConnection()
	{
		var info = GatewayInfo.Read(InfoPath);
		return new GatewayConnection(info.Port, info.Token, TimeSpan.FromMinutes(10));
	}
}
