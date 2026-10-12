using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
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
			foreach (var segment in _attached)
			{
				segment.PropertyChanged += OnSegmentChanged;
				_watched.Add(segment);
			}
		}

		private void Unwatch()
		{
			foreach (var segment in _watched) segment.PropertyChanged -= OnSegmentChanged;
			_watched.Clear();
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

		private void Relay()
		{
			_relayQueued = false;
			if (_attached is null) return; // detached while queued — Attach re-lays itself
			Unwatch();
			Watch();
			Layout();
		}

		private void OnSegmentChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName is not (nameof(RadarFrameSegment.Fill) or nameof(RadarFrameSegment.IsReady))) return;
			if (sender is RadarFrameSegment segment && _attached is not null)
			{
				var i = _attached.IndexOf(segment);
				if (i >= 0) Paint(i);
			}
		}

		private void MoveLit()
		{
			var was = _litIndex;
			_litIndex = LitIndex();
			if (was == _litIndex) return;
			if (was >= 0) PaintBlock(was);
			if (_litIndex >= 0) PaintBlock(_litIndex);
			Flush();
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
			for (var s = 0; s < count; s++) PaintBlock(s); // every dot is in exactly one block, so every dot is repainted
			Flush(); // ONCE for the whole re-lay, not per block
		}

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

		// One frame's block repainted and shown — a segment's own Fill/IsReady change.
		private void Paint(int s)
		{
			PaintBlock(s);
			Flush();
		}

		// One block's dots from its segment: the current frame whole, a ready frame whole, else the first
		// round(Fill × dots) as filling, the rest empty. Moves circles only — the CALLER flushes, once per batch.
		private void PaintBlock(int s)
		{
			if (_attached is null || s < 0 || s >= _blocks.Count || s >= _attached.Count) return;
			var segment = _attached[s];
			var block = _blocks[s];
			var lit = (int)Math.Round(Math.Clamp(segment.Fill, 0, 1) * block.Length);
			for (var i = 0; i < block.Length; i++)
			{
				SetDot(block[i], s == _litIndex ? DotState.Current
					: segment.IsReady ? DotState.Ready
					: i < lit ? DotState.Filling
					: DotState.Empty);
			}
		}
	}
}
