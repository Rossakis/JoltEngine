using Microsoft.Xna.Framework;
using static Voltage.Editor.Gateway.GatewayValues;

namespace Voltage.Editor.Gateway.Commands;

/// <summary>The editor camera: where the game view is looking.</summary>
internal static class ViewCommands
{
	public static void Register(GatewayCommandTable table)
	{
		table.Add("view.get", "Camera position and zoom of the game view.", (_, _) => State());

		table.Add("view.set", "Move or zoom the editor camera. params: x, y, zoom (scale factor, 1 = native pixels)", (args, ctx) =>
		{
			var camera = RequireCamera();
			var x = args.OptFloat("x");
			var y = args.OptFloat("y");
			if (x.HasValue || y.HasValue)
				MoveTo(ctx, new Vector2(x ?? camera.Position.X, y ?? camera.Position.Y));
			if (args.Has("zoom"))
				camera.RawZoom = args.Float("zoom");
			return State();
		});

		table.Add("view.focus", "Center the editor camera on an entity. params: entity, zoom (scale factor)", (args, ctx) =>
		{
			var camera = RequireCamera();
			var entity = ResolveEntity(args.Require("entity"));
			MoveTo(ctx, entity.Transform.Position);
			if (args.Has("zoom"))
				camera.RawZoom = args.Float("zoom");
			return State();
		});
	}

	private static Camera RequireCamera() => Core.Scene?.Camera ?? throw new GatewayException("no scene loaded");

	/// <summary>Sets both the camera and the editor's smoothed target so the view does not drift back.</summary>
	private static void MoveTo(GatewayContext ctx, Vector2 position)
	{
		RequireCamera().Position = position;
		ctx.ImGui.CameraTargetPosition = position;
	}

	private static object State()
	{
		var camera = RequireCamera();
		return new
		{
			x = camera.Position.X,
			y = camera.Position.Y,
			zoom = camera.RawZoom,
			normalizedZoom = camera.Zoom,
			editMode = Core.IsEditMode
		};
	}
}
