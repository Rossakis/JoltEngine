using System;
using ImGuiNET;
using Voltage.Editor.Utils;
using Num = System.Numerics;

namespace Voltage.Editor.Inspectors.TypeInspectors
{
	/// <summary>
	/// Shared entity-picker UI used by every reference field that targets an <see cref="Entity"/> or
	/// <see cref="Transform"/> - both single fields (<see cref="EntityReferenceTypeInspector"/>) and list
	/// elements (<see cref="ListInspector"/>).
	/// </summary>
	internal static class EntityHierarchyPicker
	{
		public static bool DrawBody(string title, Type valueType, Entity current, ref string search,
			out bool cleared, out Entity picked)
		{
			cleared = false;
			picked = null;

			ImGuiSafe.TextColoredSafe(new Num.Vector4(0.4f, 1f, 0.6f, 1f), $"{title}  ({valueType.Name})");
			ImGui.Separator();

			ImGui.SetNextItemWidth(-1);
			Gui.InputTextWithHint("##refsearch", "Search...", ref search, 128);
			ImGui.Separator();

			if (Gui.Selectable($"  None ({valueType.Name})", current == null))
			{
				cleared = true;
				return true;
			}

			ImGui.Separator();

			var scene = Core.Scene;
			if (scene == null)
				return false;

			bool filtering = !string.IsNullOrEmpty(search);
			if (filtering)
			{
				string lower = search.ToLowerInvariant();
				for (int e = 0; e < scene.Entities.Count; e++)
				{
					var entity = scene.Entities[e];
					if (!entity.Name.ToLowerInvariant().Contains(lower))
						continue;

					ImGui.PushID((int)entity.Id);
					bool chosen = Gui.Selectable($"  {entity.Name}", entity == current);
					ImGui.PopID();

					if (chosen)
					{
						picked = entity;
						return true;
					}
				}
			}
			else
			{
				for (int e = 0; e < scene.Entities.Count; e++)
				{
					var entity = scene.Entities[e];
					if (entity.Transform.Parent == null && DrawTreeNode(entity, current, out picked))
						return true;
				}
			}

			return false;
		}

		/// <summary>
		/// Recursively draws one entity (and its descendants) as a tree node, mirroring the Scene Graph
		/// layout. Returns <c>true</c> and sets <paramref name="picked"/> when this node or a descendant is
		/// clicked. The ImGui tree/ID stack is always left balanced, even when unwinding after a click.
		/// </summary>
		static bool DrawTreeNode(Entity entity, Entity current, out Entity picked)
		{
			picked = null;
			bool isSelected = entity == current;
			bool hasChildren = entity.Transform.ChildCount > 0;
			bool result = false;

			ImGui.PushID((int)entity.Id);

			if (hasChildren)
			{
				bool nodeOpen = Gui.TreeNodeEx(entity.Name,
					ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth
					| (isSelected ? ImGuiTreeNodeFlags.Selected : 0));

				// Click on the label (not the arrow) selects the node.
				if (ImGui.IsItemClicked(ImGuiMouseButton.Left) &&
				    ImGui.GetMousePos().X - ImGui.GetItemRectMin().X > ImGui.GetTreeNodeToLabelSpacing())
				{
					picked = entity;
					result = true;
				}

				if (nodeOpen)
				{
					if (!result)
					{
						for (int i = 0; i < entity.Transform.ChildCount; i++)
						{
							if (DrawTreeNode(entity.Transform.GetChild(i).Entity, current, out picked))
							{
								result = true;
								break;
							}
						}
					}

					ImGui.TreePop();
				}
			}
			else
			{
				Gui.TreeNodeEx(entity.Name,
					ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen
					| ImGuiTreeNodeFlags.SpanAvailWidth
					| (isSelected ? ImGuiTreeNodeFlags.Selected : 0));

				if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
				{
					picked = entity;
					result = true;
				}
			}

			ImGui.PopID();
			return result;
		}
	}
}
