using Microsoft.Xna.Framework;
using static Voltage.Editor.Gateway.GatewayValues;
using Voltage.Gateway;

namespace Voltage.Editor.Gateway.Commands;

/// <summary>The editor camera: where the game view is looking.</summary>
internal static class ViewCommands
{
	public static void Register(GatewayCommandTable table)
	{
		table.Add("view.get", "Camera position and zoom of the game view.", (_, _) => State()).ReadOnly();

		table.Add("view.set", "Move or zoom the editor camera.", (args, ctx) =>
		{
			var camera = RequireCamera();
			var x = args.OptFloat("x");
			var y = args.OptFloat("y");
			if (x.HasValue || y.HasValue)
				MoveTo(ctx, new Vector2(x ?? camera.Position.X, y ?? camera.Position.Y));
			if (args.Has("zoom"))
				camera.RawZoom = args.Float("zoom");
			return State();
		}, P.Float("x", "World x; unchanged when omitted"), P.Float("y", "World y; unchanged when omitted"), ZoomParam);

		table.Add("view.focus", "Center the editor camera on an entity.", (args, ctx) =>
		{
			var camera = RequireCamera();
			var entity = ResolveEntity(args.Require("entity"));
			MoveTo(ctx, entity.Transform.Position);
			if (args.Has("zoom"))
				camera.RawZoom = args.Float("zoom");
			return State();
		}, P.Str("entity", "Entity id, GUID or name", required: true), ZoomParam);
	}

	private static readonly GatewayParam ZoomParam = P.Float("zoom", "Scale factor, 1 = native pixels; unchanged when omitted");

	private static Camera RequireCamera() => Core.Scene?.Camera ?? throw new GatewayException("no scene loaded");

	/// <summary>Sets both the camera and the editor's smoothed target so the view does not drift back.</summary>
	private static void MoveTo(GatewayContext ctx, Vector2 position)
	{
		RequireCamera().Position = position;
		ctx.ImGui().CameraTargetPosition = position;
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
