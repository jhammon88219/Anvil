using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Anvil.Services;
using Anvil.ViewModels;

namespace Anvil.Controls.Primitives
{
	/// <summary>The order a block's dots light in as its frame loads.</summary>
	public enum DotFillOrder
	{
		/// <summary>Column by column, each column from the bottom up — a block fills like the loop: left to right.</summary>
		LeftToRight,
		/// <summary>Row by row from the bottom, each row left to right — a block fills like a level meter.</summary>
		BottomToTop,
	}

	/// <summary>
	/// The loop scrubber in the dot language (see DotMatrixScrubber.xaml): one block of dots per
	/// <see cref="RadarFrameSegment"/>, filling with its <see cref="RadarFrameSegment.Fill"/>, the
	/// <see cref="CurrentIndex"/> block lit whole. Drawing only — the host maps the pointer.
	/// </summary>
	public sealed partial class DotMatrixScrubber : UserControl
	{
		private enum DotState : byte { Empty, Filling, Ready, Current }

		// THE FIELD: every dot's CIRCLE, flat (index = column × rows + row), and each dot's last-applied state. The dots
		// are drawn by the FOUR PATHS declared in the XAML, one per state; a dot's circle lives in the geometry of the path
		// for its state, and a state change MOVES it (only on change). ⚠️ Computed ONCE per shape (width, rows, dot size +
		// gap) and REUSED across loops: a new loop only re-deals which frame owns which columns. It used to be torn down
		// and rebuilt (~525 Ellipses) on every re-lay, and a re-lay ran per collection event — measured 2026-10-10 as
		// 1-1.7 s UI-thread freezes on every site load.
		private EllipseGeometry[] _dots = Array.Empty<EllipseGeometry>();
		private DotState[] _dotStates = Array.Empty<DotState>();
		// Per state, the circles its path draws (index = (int)DotState).
		private readonly GeometryGroup[] _groups = new GeometryGroup[4];
		private readonly bool[] _dirty = new bool[4]; // a group whose circles moved since its path last took it
		// The XAML's four paths, index = (int)DotState — set once after InitializeComponent.
		private readonly Path[] _paths;
		private (int Columns, int Rows, double Pitch, double Left, double Size) _fieldShape;
		// Per segment, its dots' FIELD indices IN FILL ORDER.
		private readonly List<int[]> _blocks = new();
		private ObservableCollection<RadarFrameSegment>? _attached;
		private readonly List<RadarFrameSegment> _watched = new();
		// Each watched segment's index, so a segment's change finds its block without searching the loop (by reference —
		// a segment is one frame's object, never compared by value). Rebuilt with the watch list on every re-lay.
		private readonly Dictionary<RadarFrameSegment, int> _indexOf = new(ReferenceEqualityComparer.Instance);
		private int _litIndex = -1;

		public DotMatrixScrubber()
		{
			InitializeComponent();
			_paths = new[] { EmptyDots, FillingDots, ReadyDots, CurrentDots };
			SizeChanged += (_, e) => { if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 0.5) Layout(); };
			Loaded += (_, _) => Attach();
			Unloaded += (_, _) => Detach(); // the VM's collection outlives a Settings window — never leak into it
			Height = BlockHeight();
		}

		/// <summary>The loop's frames (RadarViewModel.Segments, or a preview's sample).</summary>
		public ObservableCollection<RadarFrameSegment>? Segments
		{
			get => (ObservableCollection<RadarFrameSegment>?)GetValue(SegmentsProperty);
			set => SetValue(SegmentsProperty, value);
		}

		public static readonly DependencyProperty SegmentsProperty =
			DependencyProperty.Register(nameof(Segments), typeof(ObservableCollection<RadarFrameSegment>), typeof(DotMatrixScrubber),
				new PropertyMetadata(null, (d, _) => ((DotMatrixScrubber)d).Attach()));

		/// <summary>The frame on screen (RadarViewModel.CurrentFrameIndex); its block lights whole.</summary>
		public double CurrentIndex
		{
			get => (double)GetValue(CurrentIndexProperty);
			set => SetValue(CurrentIndexProperty, value);
		}

		public static readonly DependencyProperty CurrentIndexProperty =
			DependencyProperty.Register(nameof(CurrentIndex), typeof(double), typeof(DotMatrixScrubber),
				new PropertyMetadata(0.0, (d, _) => ((DotMatrixScrubber)d).MoveLit()));

		/// <summary>The order a block's dots light in. LeftToRight by default; BottomToTop is there to try.</summary>
		public DotFillOrder FillOrder
		{
			get => (DotFillOrder)GetValue(FillOrderProperty);
			set => SetValue(FillOrderProperty, value);
		}

		public static readonly DependencyProperty FillOrderProperty =
			DependencyProperty.Register(nameof(FillOrder), typeof(DotFillOrder), typeof(DotMatrixScrubber),
				new PropertyMetadata(DotFillOrder.LeftToRight, OnShapeChanged));

		/// <summary>Dots down. 5 = 17.5 DIP tall at the default size and gap.</summary>
		public int Rows
		{
			get => (int)GetValue(RowsProperty);
			set => SetValue(RowsProperty, value);
		}

		public static readonly DependencyProperty RowsProperty =
			DependencyProperty.Register(nameof(Rows), typeof(int), typeof(DotMatrixScrubber),
				new PropertyMetadata(5, OnShapeChanged));

		/// <summary>Each dot's diameter, in DIPs — the separators' 1.5.</summary>
		public double DotSize
		{
			get => (double)GetValue(DotSizeProperty);
			set => SetValue(DotSizeProperty, value);
		}

		public static readonly DependencyProperty DotSizeProperty =
			DependencyProperty.Register(nameof(DotSize), typeof(double), typeof(DotMatrixScrubber),
				new PropertyMetadata(1.5, OnShapeChanged));

		/// <summary>The space between neighbouring dots in a block, in DIPs — the separators' 2.5.</summary>
		public double DotGap
		{
			get => (double)GetValue(DotGapProperty);
			set => SetValue(DotGapProperty, value);
		}

		public static readonly DependencyProperty DotGapProperty =
			DependencyProperty.Register(nameof(DotGap), typeof(double), typeof(DotMatrixScrubber),
				new PropertyMetadata(2.5, OnShapeChanged));

		private static void OnShapeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
		{
			var self = (DotMatrixScrubber)d;
			self.Height = self.BlockHeight();
			self.Layout();
		}

		private double BlockHeight() => Math.Max(1, Rows) * DotSize + Math.Max(0, Rows - 1) * DotGap;

		/// <summary>The frame under <paramref name="x"/> (DIPs from this control's left edge), clamped to the loop —
		/// the host's seek reads this so a click lands on the columns drawn under it (the SAME owner table as Layout).
		/// -1 with no loop.</summary>
		public int IndexAt(double x)
		{
			if (_owner.Length == 0 || _blocks.Count == 0) return -1;
			var pitch = DotSize + DotGap;
			// Nearest column, the half-gap either side of a dot counting as that dot's.
			var column = Math.Clamp((int)Math.Floor((x - _fieldLeft + DotGap / 2) / pitch), 0, _owner.Length - 1);
			return Math.Clamp(_owner[column], 0, _blocks.Count - 1);
		}

		// ---- Segment tracking ----

		private void Attach()
		{
			Detach();
			if (!IsLoaded || Segments is not { } segments)
			{
				Layout();
				return;
			}
			_attached = segments;
			_attached.CollectionChanged += OnSegmentsChanged;
			Watch();
			Layout();
		}

		private void Detach()
		{
			if (_attached is not null) _attached.CollectionChanged -= OnSegmentsChanged;
			_attached = null;
			Unwatch();
		}

		private void Watch()
		{
			if (_attached is null) return;
			for (var i = 0; i < _attached.Count; i++)
			{
				var segment = _attached[i];
				segment.PropertyChanged += OnSegmentChanged;
				_watched.Add(segment);
				_indexOf[segment] = i;
			}
		}

		private void Unwatch()
		{
			foreach (var segment in _watched) segment.PropertyChanged -= OnSegmentChanged;
			_watched.Clear();
			_indexOf.Clear();
		}

		// A new loop / a remap / a live append: re-lay every block (the slot width changes with the count).
		// ⚠️ ONCE PER BURST, queued: RadarViewModel.RebuildSegments is a Clear + one Add per frame — N + 1 events for one
		// new loop — and re-laying (and re-watching every segment) on each was the bulk of the site-load freeze. A Paint
		// in between may land on the old blocks for a tick; the queued re-lay repaints every block.
		private bool _relayQueued;

		private void OnSegmentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
		{
			if (_relayQueued) return;
			_relayQueued = true;
			if (DispatcherQueue?.TryEnqueue(Relay) != true) Relay(); // no queue (shutting down): re-lay now
		}

		// PERF PROBE (perf.ui "scrubs"/"scrubMs"): the three paint entry points below are timed. Grep RadarPerfCounters.
		private void Relay()
		{
			_relayQueued = false;
			if (_attached is null) return; // detached while queued — Attach re-lays itself
			var t = RadarPerfCounters.BeginScrub();
			Unwatch();
			Watch();
			Layout();
			RadarPerfCounters.EndScrub(t, RadarPerfCounters.ScrubRelay);
		}

		// A segment's Fill / IsReady changed: repaint its block — ⚠️ BATCHED, one paint per dispatcher turn. A loading loop
		// moves many segments at once (RefreshSegmentFill walks them all, several times a second), and painting + flushing
		// each on its own re-handed the paths their whole group once PER SEGMENT (perf audit issue 6, 2026-10-13).
		private readonly HashSet<int> _pendingPaints = new();
		private bool _paintQueued;

		private void OnSegmentChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName is not (nameof(RadarFrameSegment.Fill) or nameof(RadarFrameSegment.IsReady))) return;
			if (sender is not RadarFrameSegment segment || !_indexOf.TryGetValue(segment, out var i)) return;
			_pendingPaints.Add(i);
			if (_paintQueued) return;
			_paintQueued = true;
			if (DispatcherQueue?.TryEnqueue(PaintPending) != true) PaintPending(); // no queue (shutting down): paint now
		}

		private void PaintPending()
		{
			_paintQueued = false;
			if (_pendingPaints.Count == 0) return;
			// A queued re-lay repaints every block anyway (and these indices may be the OLD loop's).
			if (_relayQueued || _attached is null) { _pendingPaints.Clear(); return; }
			var t = RadarPerfCounters.BeginScrub();
			foreach (var s in _pendingPaints) PaintBlock(s);
			_pendingPaints.Clear();
			Flush();
			RadarPerfCounters.EndScrub(t, RadarPerfCounters.ScrubPaint);
		}

		private void MoveLit()
		{
			var was = _litIndex;
			_litIndex = LitIndex();
			if (was == _litIndex) return;
			var t = RadarPerfCounters.BeginScrub();
			if (was >= 0) PaintBlock(was);
			if (_litIndex >= 0) PaintBlock(_litIndex);
			Flush();
			RadarPerfCounters.EndScrub(t, RadarPerfCounters.ScrubLit);
		}

		private int LitIndex() =>
			_blocks.Count == 0 ? -1 : Math.Clamp((int)Math.Round(CurrentIndex), 0, _blocks.Count - 1);

		// ---- Layout: a FIXED field, frames own runs of its columns ----
		// The field is every column the width holds at the pitch, centred — it depends on the WIDTH alone, so the
		// scrubber's two edges never move whatever the frame count (and an empty field shows with no loop). Column c
		// belongs to frame floor(c × count ÷ columns): shares differ by at most ONE column, the spares spread evenly
		// through the loop rather than piled at an end, and no dot is ever blank. PastCast's cap follows the user's Radar memory
		// budget (RadarMemoryBudget; ~40–100 frames typical) — a few columns each; past one frame per column, frames share
		// (some own none).
		private double _fieldLeft;
		private int[] _owner = Array.Empty<int>();

		private void Layout()
		{
			if (Dots is null) return; // a DP set before InitializeComponent
			_blocks.Clear();
			_owner = Array.Empty<int>();

			var width = ActualWidth;
			if (width <= 0)
			{
				_litIndex = -1;
				return;
			}

			var count = _attached?.Count ?? 0;
			var rows = Math.Max(1, Rows);
			var pitch = DotSize + DotGap;
			var fieldColumns = Math.Max(1, (int)Math.Floor((width + DotGap) / pitch));
			_fieldLeft = Math.Round((width - (fieldColumns * pitch - DotGap)) / 2);
			EnsureField(fieldColumns, rows, pitch);

			if (count == 0)
			{
				// No loop: the bare field, every dot empty.
				for (var d = 0; d < _dots.Length; d++) SetDot(d, DotState.Empty);
				Flush();
				_litIndex = -1;
				return;
			}

			_owner = new int[fieldColumns];
			var firstColumn = new int[count];
			var columnCount = new int[count];
			for (var c = 0; c < fieldColumns; c++)
			{
				var s = (int)((long)c * count / fieldColumns);
				if (columnCount[s]++ == 0) firstColumn[s] = c;
				_owner[c] = s;
			}

			for (var s = 0; s < count; s++)
			{
				var columns = columnCount[s];
				var block = new int[columns * rows];
				for (var i = 0; i < block.Length; i++)
				{
					// i in FILL ORDER → (column within the frame, row-from-bottom).
					int column, fromBottom;
					if (FillOrder == DotFillOrder.BottomToTop)
					{
						fromBottom = i / columns;
						column = i % columns;
					}
					else
					{
						column = i / rows;
						fromBottom = i % rows;
					}
					block[i] = (firstColumn[s] + column) * rows + (rows - 1 - fromBottom);
				}
				_blocks.Add(block);
			}

			_litIndex = LitIndex();
			_pendingPaints.Clear(); // every block is repainted below
			RepaintAll(count);
			Flush(); // ONCE for the whole re-lay, not per block
		}

		// Every block repainted at once (a re-lay). Every dot is in exactly one block, so this is every dot's state.
		// ⚠️ When MANY dots change (a new loop: most of the field), the four circle lists are REBUILT in one pass —
		// 4 Clears + one Add per dot — instead of moving dots one by one, each move a Remove that SEARCHES its group's
		// list (O(n²) over ~525 dots; perf audit issue 6, 2026-10-13). A few changes still move just those dots.
		private void RepaintAll(int count)
		{
			if (_attached is null) return;
			var target = new DotState[_dots.Length];
			for (var s = 0; s < count && s < _blocks.Count && s < _attached.Count; s++)
			{
				var segment = _attached[s];
				var block = _blocks[s];
				var lit = FilledDots(segment, block.Length);
				for (var i = 0; i < block.Length; i++) target[block[i]] = StateOf(segment, s, i, lit);
			}

			var changed = 0;
			for (var d = 0; d < target.Length; d++) { if (target[d] != _dotStates[d]) changed++; }
			if (changed == 0) return;
			if (changed * 4 < target.Length)
			{
				for (var d = 0; d < target.Length; d++) SetDot(d, target[d]);
				return;
			}
			foreach (var group in _groups) group.Children.Clear();
			for (var d = 0; d < target.Length; d++)
			{
				_groups[(int)target[d]].Children.Add(_dots[d]);
				_dotStates[d] = target[d];
			}
			for (var g = 0; g < _dirty.Length; g++) _dirty[g] = true;
		}

		private static int FilledDots(RadarFrameSegment segment, int dots) =>
			(int)Math.Round(Math.Clamp(segment.Fill, 0, 1) * dots);

		// THE paint rule for dot i (fill order) of segment s's block: the current frame whole, a ready frame whole, else the
		// first `lit` dots filling and the rest empty. PaintBlock and RepaintAll both read it.
		private DotState StateOf(RadarFrameSegment segment, int s, int i, int lit) =>
			s == _litIndex ? DotState.Current
			: segment.IsReady ? DotState.Ready
			: i < lit ? DotState.Filling
			: DotState.Empty;

		// (Re)computes the field only when its SHAPE changed — see _dots. Fresh dots start empty (all in EmptyDots).
		private void EnsureField(int columns, int rows, double pitch)
		{
			var shape = (columns, rows, pitch, _fieldLeft, DotSize);
			if (_dots.Length == columns * rows && _fieldShape == shape) return;
			_fieldShape = shape;
			for (var g = 0; g < _groups.Length; g++) _groups[g] = new GeometryGroup();
			_dots = new EllipseGeometry[columns * rows];
			_dotStates = new DotState[_dots.Length]; // all Empty, as computed
			var radius = DotSize / 2;
			for (var c = 0; c < columns; c++)
			{
				for (var r = 0; r < rows; r++)
				{
					var dot = new EllipseGeometry
					{
						Center = new global::Windows.Foundation.Point(_fieldLeft + c * pitch + radius, r * pitch + radius),
						RadiusX = radius,
						RadiusY = radius,
					};
					_groups[(int)DotState.Empty].Children.Add(dot);
					_dots[c * rows + r] = dot;
				}
			}
			for (var g = 0; g < _paths.Length; g++) _paths[g].Data = _groups[g];
		}

		// Move dot d's circle to the path for its new state.
		private void SetDot(int d, DotState state)
		{
			var was = _dotStates[d];
			if (was == state) return;
			_dotStates[d] = state;
			_groups[(int)was].Children.Remove(_dots[d]);
			_groups[(int)state].Children.Add(_dots[d]);
			_dirty[(int)was] = _dirty[(int)state] = true;
		}

		// Hand each path whose circles moved its group again, so it re-draws from the new set. Called once after each
		// batch of SetDots (a paint, a lit move or a re-lay), never per dot or per block.
		private void Flush()
		{
			for (var g = 0; g < _groups.Length; g++)
			{
				if (!_dirty[g]) continue;
				_dirty[g] = false;
				_paths[g].Data = null;
				_paths[g].Data = _groups[g];
			}
		}

		// One block's dots from its segment (StateOf). Moves circles only — the CALLER flushes, once per batch.
		private void PaintBlock(int s)
		{
			if (_attached is null || s < 0 || s >= _blocks.Count || s >= _attached.Count) return;
			var segment = _attached[s];
			var block = _blocks[s];
			var lit = FilledDots(segment, block.Length);
			for (var i = 0; i < block.Length; i++) SetDot(block[i], StateOf(segment, s, i, lit));
		}
	}
}
