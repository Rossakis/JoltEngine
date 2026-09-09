using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using static Voltage.Editor.Tests.EditorSession;

namespace Voltage.Editor.Tests;

[TestFixture]
public class BasicsTests
{
	[Test]
	public void Ping_answers()
	{
		Assert.That(Call("ping").GetProperty("pong").GetBoolean(), Is.True);
	}

	[Test]
	public void Status_reports_headless_and_the_project()
	{
		var status = Call("status");
		Assert.That(status.GetProperty("headless").GetBoolean(), Is.True);
		Assert.That(status.GetProperty("noPrompts").GetBoolean(), Is.True);
		Assert.That(status.GetProperty("project").GetProperty("name").GetString(), Is.EqualTo("GatewaySmoke"));
	}

	[Test]
	public void Commands_carry_params_and_flags()
	{
		var commands = Call("commands").EnumerateArray().ToList();
		Assert.That(commands.Count, Is.GreaterThan(80));
		var click = commands.Single(c => c.GetProperty("name").GetString() == "ui.click");
		Assert.That(click.GetProperty("params").EnumerateArray().Any(p => p.GetProperty("name").GetString() == "label"), Is.True);
		var list = commands.Single(c => c.GetProperty("name").GetString() == "entity.list");
		Assert.That(list.GetProperty("readOnly").GetBoolean(), Is.True);
	}
}

[TestFixture]
public class SceneEntityTests
{
	[Test]
	public void Entity_create_list_set_get()
	{
		var created = Call("entity.create", new { name = "Crate", x = 10, y = 20 });
		var id = created.GetProperty("id").GetUInt32();
		Assert.That(Call("entity.list").EnumerateArray().Any(e => e.GetProperty("id").GetUInt32() == id), Is.True);

		Call("entity.set", new { entity = "Crate", x = 33, y = 44 });
		var detail = Call("entity.get", new { entity = "Crate" });
		Assert.That(detail.GetProperty("position").GetProperty("x").GetSingle(), Is.EqualTo(33f).Within(0.01f));
		Assert.That(detail.GetProperty("position").GetProperty("y").GetSingle(), Is.EqualTo(44f).Within(0.01f));
	}

	[Test]
	public void Component_add_set_get()
	{
		Call("entity.create", new { name = "Sprite" });
		var added = Call("component.add", new { entity = "Sprite", type = "SpriteRenderer" });
		Assert.That(added.GetProperty("type").GetString(), Does.Contain("SpriteRenderer"));

		Call("component.set", new { entity = "Sprite", type = "SpriteRenderer", member = "LayerDepth", value = 0.25 });
		var values = Call("component.get", new { entity = "Sprite", type = "SpriteRenderer" }).GetProperty("values");
		Assert.That(values.GetProperty("LayerDepth").GetSingle(), Is.EqualTo(0.25f).Within(0.001f));
	}

	[Test]
	public void Undo_and_redo_round_trip()
	{
		var before = Call("entity.list").GetArrayLength();
		Call("entity.create", new { name = "Temporary" });
		Assert.That(Call("entity.list").GetArrayLength(), Is.EqualTo(before + 1));

		Assert.That(Call("undo").GetProperty("done").GetBoolean(), Is.True);
		Assert.That(Call("entity.list").GetArrayLength(), Is.EqualTo(before));

		Assert.That(Call("redo").GetProperty("done").GetBoolean(), Is.True);
		Assert.That(Call("entity.list").GetArrayLength(), Is.EqualTo(before + 1));
	}

	[Test]
	public void Batch_with_undo_group_is_one_undo_step()
	{
		var before = Call("entity.list").GetArrayLength();
		var result = Call("batch", new
		{
			undoGroup = "two crates",
			requests = new object[]
			{
				new { method = "entity.create", @params = new { name = "BatchA" } },
				new { method = "entity.create", @params = new { name = "BatchB" } }
			}
		});
		Assert.That(result.GetProperty("results").GetArrayLength(), Is.EqualTo(2));
		Assert.That(result.TryGetProperty("failed", out var failed) && failed.ValueKind != JsonValueKind.Null, Is.False);
		Assert.That(Call("entity.list").GetArrayLength(), Is.EqualTo(before + 2));

		Call("undo");
		Assert.That(Call("entity.list").GetArrayLength(), Is.EqualTo(before));
	}
}

[TestFixture]
public class EventTests
{
	[Test]
	public void Wait_event_resolves_on_selection()
	{
		Call("entity.create", new { name = "Waiter" });
		Call("entity.deselect");

		using var waiter = Connect();
		var pending = Task.Run(() => waiter.Call("wait.event", JsonSerializer.SerializeToElement(new { name = "selection.*", timeout = 20 })));
		Thread.Sleep(500);
		Call("entity.select", new { entity = "Waiter" });

		Assert.That(pending.Wait(TimeSpan.FromSeconds(20)), Is.True, "wait.event did not resolve");
		Assert.That(pending.Result.GetProperty("name").GetString(), Is.EqualTo("selection.changed"));
	}

	[Test]
	public void Input_script_can_wait_for_an_event()
	{
		Call("entity.create", new { name = "Clickable" });
		Call("entity.deselect");

		using var runner = Connect();
		var script = Task.Run(() => runner.Call("input.script", JsonSerializer.SerializeToElement(new
		{
			steps = new object[]
			{
				new { action = "move", x = 5, y = 5 },
				new { action = "waitEvent", name = "selection.*", timeout = 10 },
				new { action = "release" }
			}
		})));
		Thread.Sleep(500);
		Assert.That(Call("input.state").GetProperty("waitingFor").GetString(), Is.EqualTo("selection.*"));

		Call("entity.select", new { entity = "Clickable" });
		Assert.That(script.Wait(TimeSpan.FromSeconds(15)), Is.True, "the script did not finish after the event");
		Assert.That(script.Result.GetProperty("captured").GetBoolean(), Is.False);
	}
}

[TestFixture]
public class UiTests
{
	[Test]
	public void Windows_and_tree_show_the_scene_graph()
	{
		Call("entity.create", new { name = "TreeEntity" });
		WaitForWidget("TreeEntity (0)", "Scene Graph");

		var windows = Call("ui.windows").EnumerateArray().Select(w => w.GetProperty("name").GetString()).ToList();
		Assert.That(windows, Does.Contain("Scene Graph"));

		var tree = Call("ui.tree", new { window = "Scene Graph" });
		var labels = tree.GetProperty("windows").EnumerateArray()
			.SelectMany(w => w.GetProperty("items").EnumerateArray())
			.Select(i => i.GetProperty("label").GetString()).ToList();
		Assert.That(labels.Any(l => l.StartsWith("TreeEntity", StringComparison.Ordinal)), Is.True, string.Join(", ", labels));
	}

	[Test]
	public void Click_by_label_selects_the_entity()
	{
		Call("entity.create", new { name = "ClickMe" });
		Call("entity.deselect");

		// Filter the graph first, as a person would, so the row is on screen however long the list has grown.
		Call("ui.click", new { label = "EntitySearch", window = "Scene Graph" });
		Call("input.type", new { text = "ClickMe" });
		Thread.Sleep(300);
		var row = WaitForWidget("ClickMe (0)", "Scene Graph");
		Assert.That(row.GetProperty("y").GetInt32(), Is.LessThan(250), "the filter should have moved the row to the top");
		Call("ui.click", new { label = "ClickMe (0)", window = "Scene Graph", exact = true });
		Call("ui.click", new { label = "x", window = "Scene Graph", exact = true });
		Call("input.release");
		Assert.That(Call("entity.selected").EnumerateArray().Any(e => e.GetProperty("name").GetString() == "ClickMe"), Is.True);
	}

	[Test]
	public void Headless_layout_is_docked()
	{
		var windows = Call("ui.windows").EnumerateArray().ToList();
		var game = windows.FirstOrDefault(w => w.GetProperty("id").GetString() == "GameWindow");
		Assert.That(game.ValueKind, Is.EqualTo(JsonValueKind.Object), "no game window");
		Assert.That(game.GetProperty("docked").GetBoolean(), Is.True, "game window floats: " + game.GetRawText());
		Assert.That(windows.Any(w => w.GetProperty("name").GetString() == "Scene Graph" && w.GetProperty("docked").GetBoolean()), Is.True);
	}

	[Test]
	public void Headless_run_has_no_modals()
	{
		Assert.That(Call("ui.popups").GetArrayLength(), Is.EqualTo(0));
		Assert.That(Call("ui.windows").EnumerateArray().Any(w => w.GetProperty("modal").GetBoolean()), Is.False);
	}
}

[TestFixture]
public class ScreenshotTests
{
	[Test]
	public void Screenshot_writes_a_png_of_the_reported_size()
	{
		var path = Path.Combine(Path.GetTempPath(), $"voltage-test-{Guid.NewGuid():N}.png");
		var result = Call("screenshot", new { path, scale = 0.5 });
		try
		{
			Assert.That(File.Exists(path), Is.True);
			var bytes = File.ReadAllBytes(path);
			Assert.That(bytes.Take(8), Is.EqualTo(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }));
			var width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
			var height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
			Assert.That(width, Is.EqualTo(result.GetProperty("width").GetInt32()));
			Assert.That(height, Is.EqualTo(result.GetProperty("height").GetInt32()));
			Assert.That(bytes.Length, Is.GreaterThan(2000), "a blank frame compresses to almost nothing");
		}
		finally
		{
			File.Delete(path);
		}
	}
}

[TestFixture]
public class ScriptTests
{
	[Test]
	public void Scripts_compile_on_the_sample_project()
	{
		if (!DotnetAvailable())
			Assert.Ignore("dotnet SDK not on PATH");

		var result = Call("scripts.compile");
		var errors = result.TryGetProperty("errors", out var e) && e.ValueKind == JsonValueKind.Array ? string.Join("\n", e.EnumerateArray().Select(x => x.ToString())) : "";
		Assert.That(result.GetProperty("success").GetBoolean(), Is.True, errors);
	}

	private static bool DotnetAvailable()
	{
		try
		{
			using var process = Process.Start(new ProcessStartInfo("dotnet", "--version") { RedirectStandardOutput = true, UseShellExecute = false });
			process.WaitForExit(10_000);
			return process.ExitCode == 0;
		}
		catch (Exception)
		{
			return false;
		}
	}
}
