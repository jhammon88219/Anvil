using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Anvil.Controls.TemporalPanes.DragMoveLayerExpanders
{
	/// <summary>
	/// The ONE container of <see cref="DragMoveLayerExpander"/>s — a temporal window's LAYER STACK. It stacks them
	/// top-down <see cref="Spacing"/> apart, and its CHILD ORDER is the map's z-order (top of list = top of map).
	/// It owns everything about that order: the drag area and the drag itself (the expander's grip only reports
	/// press / move / release here), Alt+Up/Down steps, <see cref="Reordered"/>, reading (<see cref="LayerOrder"/>)
	/// and applying (<see cref="ApplyLayerOrder"/>) the saved order, and the open/closed persistence.
	///
	///   PanelScroller
	///   └ DragMoveLayerExpanderHost  (Spacing 8)
	///     ├ DragMoveLayerExpander  ⠿ › [x] Storm cells   ───●───  100%
	///     ├ DragMoveLayerExpander  ⠿ › [x] Warnings      ───●───  100%
	///     └ …                       ↕ a drag stays between the first and last VISIBLE expander
	///
	/// ⚠️ HOLDS ONLY DragMoveLayerExpanders — anything else throws at measure. A note, a box or a heading goes
	/// OUTSIDE the host (above the scroller), never in it: every child is a layer.
	/// ⚠️ A COLLAPSED child (a hidden ghost row) takes no space and no spacing, and is no drop slot — but it keeps
	/// its place in Children, so the saved order still remembers where it goes.
	/// ⚠️ <see cref="IsReorderEnabled"/> false (a pane's dock stack) = no drag, no Alt+Arrow; the order stays the full pane's.
	/// ⚠️ NOTHING MOVES IN THE VISUAL TREE DURING A DRAG. Removing an element cancels its pointer capture, so the
	/// dragged expander and the siblings it passes only TRANSLATE; the real Children.Move happens once, on release.
	/// </summary>
	public sealed partial class DragMoveLayerExpanderHost : Panel
	{
		/// <summary>The gap between two visible expanders, in px.</summary>
		public double Spacing
		{
			get => (double)GetValue(SpacingProperty);
			set => SetValue(SpacingProperty, value);
		}
		public static readonly DependencyProperty SpacingProperty =
			DependencyProperty.Register(nameof(Spacing), typeof(double), typeof(DragMoveLayerExpanderHost),
				new PropertyMetadata(8.0, (d, _) => ((DragMoveLayerExpanderHost)d).InvalidateMeasure()));

		/// <summary>Drag + Alt+Arrow re-ordering is live (default). False in a pane's DOCK STACK (rail state): there the
		/// sections show in the full pane's order, and re-ordering is full-pane only.</summary>
		public bool IsReorderEnabled
		{
			get => (bool)GetValue(IsReorderEnabledProperty);
			set => SetValue(IsReorderEnabledProperty, value);
		}
		public static readonly DependencyProperty IsReorderEnabledProperty =
			DependencyProperty.Register(nameof(IsReorderEnabled), typeof(bool), typeof(DragMoveLayerExpanderHost), new PropertyMetadata(true));

		/// <summary>An expander was moved to a new place (drag or Alt+Arrow). Read the new <see cref="LayerOrder"/>.</summary>
		public event EventHandler? Reordered;

		/// <summary>The layer ids, top first — the map's order.</summary>
		public IReadOnlyList<string> LayerOrder => Expanders.Select(x => x.LayerId).ToList();

		private IEnumerable<DragMoveLayerExpander> Expanders => Children.Cast<DragMoveLayerExpander>();

		private List<DragMoveLayerExpander> VisibleExpanders() =>
			Expanders.Where(x => x.Visibility == Visibility.Visible).ToList();

		// ── Layout: a vertical stack ─────────────────────────────────────────────────────────────────

		protected override Size MeasureOverride(Size availableSize)
		{
			double width = 0, height = 0;
			bool first = true;
			foreach (var child in Children)
			{
				if (child is not DragMoveLayerExpander)
				{
					throw new InvalidOperationException(
						$"{nameof(DragMoveLayerExpanderHost)} holds only {nameof(DragMoveLayerExpander)}s; found {child.GetType().Name}.");
				}
				child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
				if (child.Visibility == Visibility.Collapsed) { continue; }
				if (!first) { height += Spacing; }
				first = false;
				height += child.DesiredSize.Height;
				width = Math.Max(width, child.DesiredSize.Width);
			}
			return new Size(width, height);
		}

		protected override Size ArrangeOverride(Size finalSize)
		{
			double y = 0;
			bool first = true;
			foreach (var child in Children)
			{
				if (child.Visibility == Visibility.Collapsed)
				{
					child.Arrange(new Rect(0, y, finalSize.Width, 0));
					continue;
				}
				if (!first) { y += Spacing; }
				first = false;
				child.Arrange(new Rect(0, y, finalSize.Width, child.DesiredSize.Height));
				y += child.DesiredSize.Height;
			}
			return new Size(finalSize.Width, y);
		}

		// ── Drag (driven by the expander's grip) ─────────────────────────────────────────────────────

		private DragMoveLayerExpander? _dragged;
		private List<DragMoveLayerExpander> _block = new();
		private List<double> _tops = new();
		private int _from, _to;
		private double _startY;

		/// <summary>The grip was pressed at <paramref name="y"/> (host coordinates). False = no drag (fewer than 2 visible).</summary>
		internal bool BeginDrag(DragMoveLayerExpander expander, double y)
		{
			if (!IsReorderEnabled) { return false; }
			var block = VisibleExpanders();
			if (block.Count < 2 || !block.Contains(expander)) { return false; }

			_dragged = expander;
			_block = block;
			_from = _to = block.IndexOf(expander);
			_tops = block.Select(x => x.TransformToVisual(this).TransformPoint(new Point(0, 0)).Y).ToList();
			_startY = y;

			foreach (var x in block)
			{
				x.TranslationTransition = ReferenceEquals(x, expander) ? null : new Vector3Transition { Duration = TimeSpan.FromMilliseconds(140) };
			}
			Canvas.SetZIndex(expander, 1);
			return true;
		}

		/// <summary>The pointer moved to <paramref name="y"/> (host coordinates).</summary>
		internal void DragTo(double y)
		{
			if (_dragged is null) { return; }
			double h = _dragged.ActualHeight;
			double dy = y - _startY;
			// Stay inside the block: no further up than its first slot, no further down than its last.
			var last = _block[^1];
			dy = Math.Clamp(dy, _tops[0] - _tops[_from], _tops[^1] + last.ActualHeight - (_tops[_from] + h));
			_dragged.Translation = new Vector3(0, (float)dy, 0);

			// The dragged expander's LEADING EDGE decides its slot: a sibling steps aside once that edge crosses
			// the sibling's middle, and every sibling passed slides one expander-height the other way.
			// ⚠️ NOT the dragged expander's middle: the clamp above stops it at the block's end, so one as tall as
			// (or taller than) the last one could never get its middle past the last one's — the end sibling would
			// never move out of the way.
			double top = _tops[_from] + dy, bottom = top + h;
			int to = _from;
			for (int i = 0; i < _from; i++)
			{
				if (top < _tops[i] + _block[i].ActualHeight / 2) { to = i; break; }
			}
			for (int i = _block.Count - 1; i > _from; i--)
			{
				if (bottom > _tops[i] + _block[i].ActualHeight / 2) { to = i; break; }
			}
			_to = to;

			float step = (float)(h + Spacing);
			for (int i = 0; i < _block.Count; i++)
			{
				if (i == _from) { continue; }
				float shift = i >= to && i < _from ? step : i <= to && i > _from ? -step : 0;
				_block[i].Translation = new Vector3(0, shift, 0);
			}
		}

		/// <summary>The grip was released (or lost capture): land the expander in its slot.</summary>
		internal void EndDrag()
		{
			if (_dragged is null) { return; }
			var dragged = _dragged;
			_dragged = null;

			// Snap everything home with no transition, then move for real in the same tick, so the layout lands
			// exactly where the translations were showing it.
			foreach (var x in _block)
			{
				x.TranslationTransition = null;
				x.Translation = Vector3.Zero;
			}
			Canvas.SetZIndex(dragged, 0);

			if (_to != _from)
			{
				Children.Move((uint)Children.IndexOf(dragged), (uint)Children.IndexOf(_block[_to]));
				Reordered?.Invoke(this, EventArgs.Empty);
			}
			_block = new();
		}

		/// <summary>Alt+Up (−1) / Alt+Down (+1): one visible slot. False = already at that end.</summary>
		internal bool Step(DragMoveLayerExpander expander, int delta)
		{
			if (_dragged is not null || !IsReorderEnabled) { return false; }
			var block = VisibleExpanders();
			int at = block.IndexOf(expander);
			int to = at + delta;
			if (at < 0 || to < 0 || to >= block.Count) { return false; }
			Children.Move((uint)Children.IndexOf(expander), (uint)Children.IndexOf(block[to]));
			Reordered?.Invoke(this, EventArgs.Empty);
			return true;
		}

		// ── Saved order + open/closed state ──────────────────────────────────────────────────────────

		/// <summary>
		/// Re-arranges the expanders into a saved order (top first). Ids the list doesn't name keep their XAML
		/// order, after the named ones. Empty = leave the XAML order alone. Raises nothing.
		/// </summary>
		public void ApplyLayerOrder(IReadOnlyList<string> topFirst)
		{
			if (topFirst.Count == 0) { return; }
			var sorted = Expanders
				.Select((x, k) => (Expander: x, k))
				.OrderBy(t => { int r = IndexOf(topFirst, t.Expander.LayerId); return r < 0 ? int.MaxValue : r; })
				.ThenBy(t => t.k)
				.Select(t => t.Expander)
				.ToList();
			for (int j = 0; j < sorted.Count; j++)
			{
				int cur = Children.IndexOf(sorted[j]);
				if (cur != j) { Children.Move((uint)cur, (uint)j); }
			}
		}

		/// <summary>
		/// Restores each expander's saved open/closed state, then saves every later toggle. Call ONCE per host.
		/// The key is "window/LayerId", so it survives a re-order. Ghost rows are skipped (no body to open).
		/// </summary>
		public void PersistExpansion(string window, Func<string, bool?> get, Action<string, bool> set)
		{
			foreach (var expander in Expanders.Where(x => !x.IsGhost))
			{
				var key = window + "/" + expander.LayerId;
				if (get(key) is bool open) { expander.IsExpanded = open; }
				expander.RegisterPropertyChangedCallback(DragMoveLayerExpander.IsExpandedProperty,
					(d, _) => set(key, ((DragMoveLayerExpander)d).IsExpanded));
			}
		}

		private static int IndexOf(IReadOnlyList<string> list, string id)
		{
			for (int i = 0; i < list.Count; i++)
			{
				if (string.Equals(list[i], id, StringComparison.Ordinal)) { return i; }
			}
			return -1;
		}
	}
}
