using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.Layout;
using Anvil.ViewModels;

namespace Anvil.Controls.Primitives
{
	/// <summary>
	/// The original solid-cell loop scrubber (see ClassicScrubber.xaml): cells bound to <see cref="Segments"/>, the
	/// playhead over <see cref="CurrentIndex"/>. Drawing only — the host maps the pointer.
	/// </summary>
	public sealed partial class ClassicScrubber : UserControl
	{
		private ObservableCollection<RadarFrameSegment>? _attached;

		public ClassicScrubber()
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
			DependencyProperty.Register(nameof(Segments), typeof(ObservableCollection<RadarFrameSegment>), typeof(ClassicScrubber),
				new PropertyMetadata(null, (d, _) => ((ClassicScrubber)d).Attach()));

		/// <summary>The frame on screen (RadarViewModel.CurrentFrameIndex); the playhead sits over its cell.</summary>
		public double CurrentIndex
		{
			get => (double)GetValue(CurrentIndexProperty);
			set => SetValue(CurrentIndexProperty, value);
		}

		public static readonly DependencyProperty CurrentIndexProperty =
			DependencyProperty.Register(nameof(CurrentIndex), typeof(double), typeof(ClassicScrubber),
				new PropertyMetadata(0.0, (d, _) => ((ClassicScrubber)d).UpdatePlayhead()));

		private void Attach()
		{
			Detach();
			if (IsLoaded && Segments is { } segments)
			{
				_attached = segments;
				_attached.CollectionChanged += OnSegmentsChanged; // count changes -> reposition playhead
			}
			UpdatePlayhead();
		}

		private void Detach()
		{
			if (_attached is not null) _attached.CollectionChanged -= OnSegmentsChanged;
			_attached = null;
		}

		private void OnSegmentsChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdatePlayhead();

		private void OnRootSizeChanged(object sender, SizeChangedEventArgs e) => UpdatePlayhead();

		// Positions the playhead over the current segment's centre. Counts from Segments.Count and asks
		// EqualCellsPanel itself where that cell sits, so the playhead lands exactly on the midpoint the
		// panel arranged (no cumulative drift, and no second copy of its rounding rule to drift from).
		private void UpdatePlayhead()
		{
			if (Root is null || Playhead is null || PlayheadTransform is null) return;
			var count = Segments?.Count ?? 0;
			var width = Root.ActualWidth;
			if (count <= 0 || width <= 0)
			{
				Playhead.Visibility = Visibility.Collapsed;
				return;
			}
			Playhead.Visibility = Visibility.Visible;
			var idx = Math.Clamp((int)Math.Round(CurrentIndex), 0, count - 1);
			var centre = EqualCellsPanel.CellCenter(width, count, idx);
			PlayheadTransform.X = Math.Clamp(centre - Playhead.Width / 2, 0, width - Playhead.Width);
		}
	}
}
