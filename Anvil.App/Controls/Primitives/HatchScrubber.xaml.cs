using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Anvil.Layout;
using Anvil.Models;
using Anvil.ViewModels;
using Point = Windows.Foundation.Point;

namespace Anvil.Controls.Primitives
{
	/// <summary>
	/// The hatch loop scrubber (see HatchScrubber.xaml): a <see cref="HatchScrubberCell"/> per frame of
	/// <see cref="Segments"/>, a solid marker over <see cref="CurrentIndex"/>. Drawing only — the host maps the pointer.
	/// </summary>
	public sealed partial class HatchScrubber : UserControl
	{
		private ObservableCollection<RadarFrameSegment>? _attached;

		/// <summary>The cells' height, px (the root is 16, the classic scrubber's).</summary>
		public double CellHeight => 12;

		public HatchScrubber()
		{
			InitializeComponent();
			Loaded += (_, _) => Attach();
			Unloaded += (_, _) => Detach(); // the VM's collection outlives a Settings window — never leak into it
		}

		/// <summary>The loop's frames (RadarViewModel.Segments, or a preview's sample).</summary>
		public ObservableCollection<RadarFrameSegment>? Segments
		{
			get => (ObservableCollection<RadarFrameSegment>?)GetValue(SegmentsProperty);
			set => SetValue(SegmentsProperty, value);
		}

		public static readonly DependencyProperty SegmentsProperty =
			DependencyProperty.Register(nameof(Segments), typeof(ObservableCollection<RadarFrameSegment>), typeof(HatchScrubber),
				new PropertyMetadata(null, (d, _) => ((HatchScrubber)d).Attach()));

		/// <summary>The frame on screen (RadarViewModel.CurrentFrameIndex); the marker covers its cell.</summary>
		public double CurrentIndex
		{
			get => (double)GetValue(CurrentIndexProperty);
			set => SetValue(CurrentIndexProperty, value);
		}

		public static readonly DependencyProperty CurrentIndexProperty =
			DependencyProperty.Register(nameof(CurrentIndex), typeof(double), typeof(HatchScrubber),
				new PropertyMetadata(0.0, (d, _) => ((HatchScrubber)d).UpdateCurrent()));

		private void Attach()
		{
			Detach();
			if (IsLoaded && Segments is { } segments)
			{
				_attached = segments;
				_attached.CollectionChanged += OnSegmentsChanged; // count changes -> the marker's cell moves + resizes
			}
			UpdateCurrent();
		}

		private void Detach()
		{
			if (_attached is not null) _attached.CollectionChanged -= OnSegmentsChanged;
			_attached = null;
		}

		private void OnSegmentsChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateCurrent();

		private void OnRootSizeChanged(object sender, SizeChangedEventArgs e) => UpdateCurrent();

		private double _markerW = -1;

		// Covers the current cell with a solid parallelogram. Asks EqualCellsPanel itself where that cell's edges sit
		// (no second copy of its rounding rule); the shape is COMPUTED (HatchScrubberGeometry.Outline), so the Path's Data
		// is rebuilt here — only when the cell's width changes; a plain move just slides the transform.
		private void UpdateCurrent()
		{
			if (HatchScrubberRoot is null || HatchScrubberCurrent is null || HatchScrubberCurrentTransform is null) return;
			var count = Segments?.Count ?? 0;
			var width = HatchScrubberRoot.ActualWidth;
			if (count <= 0 || width <= 0)
			{
				HatchScrubberCurrent.Visibility = Visibility.Collapsed;
				return;
			}
			var idx = Math.Clamp((int)Math.Round(CurrentIndex), 0, count - 1);
			double left = EqualCellsPanel.CellEdge(width, count, idx) + 0.5;
			double w = EqualCellsPanel.CellEdge(width, count, idx + 1) - 0.5 - left; // the cell's own 0.5 px margins
			if (w <= 0)
			{
				HatchScrubberCurrent.Visibility = Visibility.Collapsed;
				return;
			}
			HatchScrubberCurrent.Visibility = Visibility.Visible;
			if (w != _markerW)
			{
				_markerW = w;
				var outline = HatchScrubberGeometry.Outline(w, CellHeight);
				var figure = new PathFigure { StartPoint = new Point(outline[0].X, outline[0].Y), IsClosed = true, IsFilled = true };
				for (int i = 1; i < outline.Length; i++) figure.Segments.Add(new LineSegment { Point = new Point(outline[i].X, outline[i].Y) });
				var shape = new PathGeometry();
				shape.Figures.Add(figure);
				HatchScrubberCurrent.Data = shape;
			}
			HatchScrubberCurrentTransform.X = left;
		}
	}
}
