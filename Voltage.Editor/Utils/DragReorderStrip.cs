using System;
using System.Collections.Generic;
using ImGuiNET;
using Num = System.Numerics;

namespace Voltage.Editor.Utils
{
	/// <summary>
	/// Drag-to-reorder feedback for any list of rows. Instead of drawing a line between two rows, the list slides
	/// open a landing strip in the seam the drop will land in, so the rows below move aside to show the slot.
	///
	/// <para>Immediate mode makes this easier than it looks: there is nothing retained to animate, but layout is
	/// rebuilt every frame, so growing one spacer's height reflows everything after it. Several gaps are kept alive
	/// at once - the seam being left shrinks by exactly as much as the new one grows - and the rows between them
	/// ride that difference, which is what reads as sliding rather than the gap teleporting.</para>
	///
	/// <para>Cost does not scale with the list: at most a handful of live gaps and one extra row, whatever the row
	/// count.</para>
	///
	/// <para>Lists that also drop INTO their rows (a tree assigning a parent) pass <c>allowInner: true</c> to
	/// <see cref="HandleRow"/>. No seam opens for those - the row is outlined instead, so "between these two" and
	/// "inside this one" never look alike.</para>
	/// </summary>
	public sealed class DragReorderStrip
	{
		/// <summary>Where the cursor sits on a row, and so what dropping there would mean.</summary>
		public enum RowZone
		{
			None,
			Above,
			Inner,
			Below
		}

		private readonly string _payloadId;
		private readonly Dictionary<int, float> _gaps = new();
		private readonly List<int> _scratch = new();

		// The seam armed this frame. Read one frame later: the cursor is found while the rows are drawn, so the
		// strip always eases toward where it was last frame - far too little lag to see.
		private int _slot = -1;

		private int _framesSinceRelease;

		public DragReorderStrip(string payloadId)
		{
			_payloadId = payloadId;
		}

		/// <summary>True from the moment a drag starts until the mouse button is let go.</summary>
		public bool IsDragging { get; private set; }

		/// <summary>How much of a row, top and bottom, counts as "between rows" when inner drops are allowed.</summary>
		public float EdgeFraction { get; set; } = 0.35f;

		/// <summary>Colour of the strip, and of the outline around a row a drop would go inside.</summary>
		public Num.Vector4 Accent { get; set; } = new(0.30f, 0.72f, 1f, 0.40f);

		/// <summary>Most rows a strip may be tall, so a large pick cannot open a hole bigger than the list.</summary>
		public int MaxStripRows { get; set; } = 3;

		/// <summary>How close to an edge the cursor has to get before the list starts creeping.</summary>
		public float ScrollMargin { get; set; } = 36f;

		/// <summary>Top speed of the creep, at the edge itself. Deliberately gentle - the rows are the target.</summary>
		public float ScrollSpeed { get; set; } = 260f;

		/// <summary>
		/// Publishes a contentless drag payload and marks the drag as running. Call inside
		/// <c>BeginDragDropSource</c>.
		/// </summary>
		/// <remarks>
		/// Only for lists that own their payload outright. Where the payload already carries something other code
		/// reads - the entity tree publishes an entity id that the reference inspectors accept - keep publishing it
		/// and call <see cref="MarkDragStarted"/> instead.
		/// </remarks>
		public unsafe void SetPayload()
		{
			byte sentinel = 1;
			ImGui.SetDragDropPayload(_payloadId, (IntPtr)(&sentinel), 1);
			MarkDragStarted();
		}

		/// <summary>Marks a drag as running when the payload is published by the caller.</summary>
		public void MarkDragStarted() => IsDragging = true;

		/// <summary>
		/// Advances the animation. Call once per frame before the rows are drawn, passing how many rows are being
		/// dragged and how tall one row is.
		/// </summary>
		public void BeginFrame(int pickedCount, float rowHeight)
		{
			// The drag ends with the button, not with the row leaving view: a dragged row can be scrolled clean off
			// the list and the drag is still running.
			//
			// One frame of grace after the button comes up, because that is the frame imgui delivers the payload
			// on. Calling the drag over immediately would let a caller tear the strip down - a list that swaps to
			// a clipper while dragging does exactly that - in the very frame the drop was meant to land on it,
			// which reads as a drop that silently does nothing every so often.
			if (IsDragging && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
			{
				if (_framesSinceRelease++ > 0)
				{
					IsDragging = false;
					_framesSinceRelease = 0;
				}
			}
			else
			{
				_framesSinceRelease = 0;
			}

			var rows = Math.Clamp(pickedCount, 1, Math.Max(1, MaxStripRows));
			Animate(IsDragging ? _slot : -1, rows * rowHeight);

			_slot = -1;
		}

		/// <summary>True while a gap is open in the seam above row <paramref name="slot"/>.</summary>
		public bool TryGetGap(int slot, out float height) => _gaps.TryGetValue(slot, out height);

		/// <summary>
		/// Creeps the current window up or down while a row is held against its top or bottom edge, so something
		/// can be dragged past the rows that fit on screen without letting go to scroll first. Speed ramps from a
		/// crawl at <see cref="ScrollMargin"/> to <see cref="ScrollSpeed"/> at the edge.
		///
		/// <para>Call it from inside the window that scrolls - for a list drawn in a child, that means inside the
		/// child - after the rows have been submitted. Does nothing unless a drag is running.</para>
		/// </summary>
		public void AutoScroll()
		{
			if (!IsDragging)
				return;

			var mouse = ImGui.GetIO().MousePos;
			var min = ImGui.GetWindowPos();
			var max = min + ImGui.GetWindowSize();

			// Dragging clean out to the side is how you get away from the list, so it stops the creep; straight up
			// or down past the edge is not, and keeps it running at full speed.
			if (mouse.X < min.X - ScrollMargin || mouse.X > max.X + ScrollMargin)
				return;

			var speed = 0f;

			if (mouse.Y < min.Y + ScrollMargin)
				speed = -(1f - Math.Clamp((mouse.Y - min.Y) / ScrollMargin, 0f, 1f));
			else if (mouse.Y > max.Y - ScrollMargin)
				speed = 1f - Math.Clamp((max.Y - mouse.Y) / ScrollMargin, 0f, 1f);

			if (speed == 0f)
				return;

			ImGui.SetScrollY(ImGui.GetScrollY() + speed * ScrollSpeed * ImGui.GetIO().DeltaTime);
		}

		/// <summary>Drops the animation immediately - call after a drop, once the rows have been renumbered.</summary>
		public void Reset()
		{
			_gaps.Clear();
			_slot = -1;
		}

		/// <summary>
		/// The drop-target side of one row - call it straight after submitting the row's item. Arms the seam the
		/// cursor is nearest, or outlines the row when the drop would go inside it. Returns the zone under the
		/// cursor (<see cref="RowZone.None"/> when this row is not a drop candidate), and sets
		/// <paramref name="dropped"/> when the payload was actually released on it this frame.
		/// </summary>
		public RowZone HandleRow(int slot, bool allowInner, out bool dropped)
		{
			dropped = false;

			if (!ImGui.BeginDragDropTarget())
				return RowZone.None;

			var min = ImGui.GetItemRectMin();
			var max = ImGui.GetItemRectMax();
			var fraction = (ImGui.GetIO().MousePos.Y - min.Y) / Math.Max(1f, max.Y - min.Y);

			RowZone zone;

			if (!allowInner)
				zone = fraction <= 0.5f ? RowZone.Above : RowZone.Below;
			else if (fraction <= EdgeFraction)
				zone = RowZone.Above;
			else if (fraction >= 1f - EdgeFraction)
				zone = RowZone.Below;
			else
				zone = RowZone.Inner;

			if (zone == RowZone.Inner)
			{
				// Nothing slides for a drop that goes INSIDE a row - the outline is what says "this becomes the
				// parent", and keeping the list still is what makes it read differently from a reorder.
				ImGui.GetWindowDrawList().AddRect(min, max, ImGui.GetColorU32(Accent), 2f, 0, 2f);
			}
			else
			{
				_slot = zone == RowZone.Above ? slot : slot + 1;
			}

			dropped = AcceptPayload();
			ImGui.EndDragDropTarget();

			return zone;
		}

		/// <summary>
		/// Draws the landing strip in the seam above row <paramref name="slot"/>, and makes it a drop target of its
		/// own. Returns true when the payload is released on it.
		///
		/// <para>The strip has to be a target: resting the cursor in the gap it just opened would otherwise count as
		/// hovering nothing, so the gap would close, the row below would spring back up under the cursor and reopen
		/// it, and the two would flicker forever.</para>
		/// </summary>
		public bool DrawStrip(int slot, float height, bool inTable)
		{
			if (height < 1f)
				return false;

			if (inTable)
			{
				ImGui.TableNextRow();
				ImGui.TableSetColumnIndex(0);
			}

			ImGui.PushStyleColor(ImGuiCol.Header, Accent);
			ImGui.PushStyleColor(ImGuiCol.HeaderHovered, Accent);
			ImGui.PushStyleColor(ImGuiCol.HeaderActive, Accent);

			// A Selectable quietly grows its box by half ItemSpacing.Y above and below, which is what keeps rows in
			// a list touching. Here it would put a fixed floor under the animation - the strip could never close -
			// and the box would reach into the rows either side and fight them for the cursor.
			ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Num.Vector2(ImGui.GetStyle().ItemSpacing.X, 0f));

			var flags = inTable ? ImGuiSelectableFlags.SpanAllColumns : ImGuiSelectableFlags.None;
			Gui.Selectable($"##dragstrip_{_payloadId}_{slot}", true, flags, new Num.Vector2(0f, height));

			ImGui.PopStyleVar();
			ImGui.PopStyleColor(3);

			if (!ImGui.BeginDragDropTarget())
				return false;

			_slot = slot;

			var dropped = AcceptPayload();
			ImGui.EndDragDropTarget();

			return dropped;
		}

		/// <summary>
		/// Eases every live gap toward its target: the armed seam opens, every other one closes. Keeping the closing
		/// ones alive is the whole trick behind the slide.
		/// </summary>
		private void Animate(int targetSlot, float targetHeight)
		{
			if (targetSlot >= 0 && !_gaps.ContainsKey(targetSlot))
				_gaps[targetSlot] = 0f;

			if (_gaps.Count == 0)
				return;

			// Exponential ease, so it settles in about a tenth of a second whatever the refresh rate.
			var rate = 1f - (float)Math.Exp(-ImGui.GetIO().DeltaTime * 22f);

			_scratch.Clear();
			_scratch.AddRange(_gaps.Keys);

			foreach (var slot in _scratch)
			{
				var current = _gaps[slot];
				var target = slot == targetSlot ? targetHeight : 0f;
				var height = current + (target - current) * rate;

				if (target <= 0f && height < 0.5f)
					_gaps.Remove(slot);
				else
					_gaps[slot] = height;
			}
		}

		/// <summary>
		/// AcceptNoDrawDefaultRect: imgui otherwise outlines every target it would accept, which is a second
		/// drop indicator drawn in a different visual language than the strip - and the two disagree, because
		/// imgui's rect marks the item under the cursor while the strip marks the seam the drop lands in.
		/// </summary>
		private unsafe bool AcceptPayload()
		{
			var payload = ImGui.AcceptDragDropPayload(_payloadId, ImGuiDragDropFlags.AcceptNoDrawDefaultRect);
			return payload.NativePtr != null;
		}
	}
}
