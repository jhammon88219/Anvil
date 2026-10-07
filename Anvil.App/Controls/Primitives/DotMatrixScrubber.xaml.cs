using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

		// Per segment, its dots IN FILL ORDER, and each dot's last-applied state (a style is re-set only on change).
		private readonly List<Ellipse[]> _blocks = new();
		private readonly List<DotState[]> _states = new();
		private ObservableCollection<RadarFrameSegment>? _attached;
		private readonly List<RadarFrameSegment> _watched = new();
		private int _litIndex = -1;
		private Style? _empty, _filling, _ready, _current;

		public DotMatrixScrubber()
		{
			InitializeComponent();
			_empty = (Style)Resources["EmptyDot"];
			_filling = (Style)Resources["FillingDot"];
			_ready = (Style)Resources["ReadyDot"];
			_current = (Style)Resources["CurrentDot"];
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
		private void OnSegmentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
		{
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
			if (was >= 0) Paint(was);
			if (_litIndex >= 0) Paint(_litIndex);
		}

		private int LitIndex() =>
			_blocks.Count == 0 ? -1 : Math.Clamp((int)Math.Round(CurrentIndex), 0, _blocks.Count - 1);

		/// <summary>The frame under <paramref name="x"/> (DIPs from this control's left edge), clamped to the loop —
		/// the host's seek reads this so a click lands on the columns drawn under it (the SAME owner table). -1 with no
		/// loop.</summary>
		public int IndexAt(double x)
		{
			if (_owner.Length == 0 || _blocks.Count == 0) return -1;
			var pitch = DotSize + DotGap;
			// Nearest column, the half-gap either side of a dot counting as that dot's.
			var column = Math.Clamp((int)Math.Floor((x - _fieldLeft + DotGap / 2) / pitch), 0, _owner.Length - 1);
			return Math.Clamp(_owner[column], 0, _blocks.Count - 1);
		}

		// ---- Layout: a FIXED field, frames own runs of its columns ----
		// The field is every column the width holds at the pitch, centred — it depends on the WIDTH alone, so the
		// scrubber's two edges never move whatever the frame count (and an empty field shows with no loop). Column c
		// belongs to frame floor(c × count ÷ columns): shares differ by at most ONE column, the spares spread evenly
		// through the loop rather than piled at an end, and no dot is ever blank. PastCast caps a loop at 40 frames
		// (RadarViewModel.PastEventMaxFrames) — 2–3 columns each; past one frame per column, frames share (some own none).
		private double _fieldLeft;
		private int[] _owner = Array.Empty<int>();

		private void Layout()
		{
			if (Dots is null) return; // a DP set before InitializeComponent
			Dots.Children.Clear();
			_blocks.Clear();
			_states.Clear();
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

			if (count == 0)
			{
				// No loop: the bare field, every dot empty.
				for (var c = 0; c < fieldColumns; c++)
				{
					for (var r = 0; r < rows; r++) Dots.Children.Add(MakeDot(c, r, pitch));
				}
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
				var dots = new Ellipse[columns * rows];
				for (var i = 0; i < dots.Length; i++)
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
					var dot = MakeDot(firstColumn[s] + column, rows - 1 - fromBottom, pitch);
					Dots.Children.Add(dot);
					dots[i] = dot;
				}
				_blocks.Add(dots);
				_states.Add(new DotState[dots.Length]); // all Empty, as built
			}

			_litIndex = LitIndex();
			for (var s = 0; s < count; s++) Paint(s);
		}

		// One empty dot at field column c, row r (0 = top).
		private Ellipse MakeDot(int column, int row, double pitch)
		{
			var dot = new Ellipse { Width = DotSize, Height = DotSize, Style = _empty };
			Canvas.SetLeft(dot, _fieldLeft + column * pitch);
			Canvas.SetTop(dot, row * pitch);
			return dot;
		}

		// One block's dots from its segment: the current frame whole, a ready frame whole, else the first
		// round(Fill × dots) as filling, the rest empty.
		private void Paint(int s)
		{
			if (_attached is null || s < 0 || s >= _blocks.Count || s >= _attached.Count) return;
			var segment = _attached[s];
			var dots = _blocks[s];
			var states = _states[s];
			var lit = (int)Math.Round(Math.Clamp(segment.Fill, 0, 1) * dots.Length);
			for (var i = 0; i < dots.Length; i++)
			{
				var state = s == _litIndex ? DotState.Current
					: segment.IsReady ? DotState.Ready
					: i < lit ? DotState.Filling
					: DotState.Empty;
				if (state == states[i]) continue;
				states[i] = state;
				dots[i].Style = state switch
				{
					DotState.Current => _current,
					DotState.Ready => _ready,
					DotState.Filling => _filling,
					_ => _empty,
				};
			}
		}
	}
}
