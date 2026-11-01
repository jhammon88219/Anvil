using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;
using Anvil.Models;
using Anvil.ViewModels;
using Color = Windows.UI.Color;
using Point = Windows.Foundation.Point;

namespace Anvil.Controls.Primitives
{
	/// <summary>
	/// The hatch loop scrubber (see HatchScrubber.xaml): the edge hatch laid along the loop, each frame's lines grown by its
	/// fill, the frame on screen's lines bolder. Drawing only — the host maps the pointer.
	/// </summary>
	public sealed partial class HatchScrubber : UserControl
	{
		private ObservableCollection<RadarFrameSegment>? _attached;
		private readonly List<RadarFrameSegment> _watched = new();

		/// <summary>The strip's height, px.</summary>
		public double Depth => HatchScrubberStrip.Depth;

		public HatchScrubber()
		{
			InitializeComponent();
			Loaded += (_, _) => { ApplyInk(); Attach(); };
			Unloaded += (_, _) => Detach(); // the VM's collection outlives a Settings window — never leak into it
			ActualThemeChanged += (_, _) => ApplyInk();
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

		/// <summary>The frame on screen (RadarViewModel.CurrentFrameIndex); its lines are drawn bolder.</summary>
		public double CurrentIndex
		{
			get => (double)GetValue(CurrentIndexProperty);
			set => SetValue(CurrentIndexProperty, value);
		}

		public static readonly DependencyProperty CurrentIndexProperty =
			DependencyProperty.Register(nameof(CurrentIndex), typeof(double), typeof(HatchScrubber),
				new PropertyMetadata(0.0, (d, _) => ((HatchScrubber)d).QueueDraw()));

		/// <summary>The line spacing (Settings → Radar Console), px at the edge's 35 px — scaled to the strip.</summary>
		public double Spacing
		{
			get => (double)GetValue(SpacingProperty);
			set => SetValue(SpacingProperty, value);
		}

		public static readonly DependencyProperty SpacingProperty =
			DependencyProperty.Register(nameof(Spacing), typeof(double), typeof(HatchScrubber),
				new PropertyMetadata(HatchScrubberLook.DefaultSpacing, (d, _) => ((HatchScrubber)d).QueueDraw()));

		/// <summary>The line weight (Settings → Radar Console), px at the edge's 35 px — scaled to the strip.</summary>
		public double Weight
		{
			get => (double)GetValue(WeightProperty);
			set => SetValue(WeightProperty, value);
		}

		public static readonly DependencyProperty WeightProperty =
			DependencyProperty.Register(nameof(Weight), typeof(double), typeof(HatchScrubber), new PropertyMetadata(HatchScrubberLook.DefaultWeight));

		/// <summary>The line colour (Settings → Radar Console): <c>#RRGGBB</c>, or "" = the accent.</summary>
		public string Ink
		{
			get => (string)GetValue(InkProperty);
			set => SetValue(InkProperty, value);
		}

		public static readonly DependencyProperty InkProperty =
			DependencyProperty.Register(nameof(Ink), typeof(string), typeof(HatchScrubber),
				new PropertyMetadata(HatchScrubberLook.Accent, (d, _) => ((HatchScrubber)d).ApplyInk()));

		/// <summary>The line opacity, 0–1 (Settings → Radar Console); the current frame's lines are always full.</summary>
		public double LineOpacity
		{
			get => (double)GetValue(LineOpacityProperty);
			set => SetValue(LineOpacityProperty, value);
		}

		public static readonly DependencyProperty LineOpacityProperty =
			DependencyProperty.Register(nameof(LineOpacity), typeof(double), typeof(HatchScrubber), new PropertyMetadata(HatchScrubberLook.DefaultOpacity / 100));

		public double LineWeight(double weight) => HatchScrubberStrip.Scaled(weight, Depth);

		public double CurrentLineWeight(double weight) => LineWeight(weight) * HatchScrubberStrip.CurrentWeight;

		// ---- Ink ----

		private void ApplyInk()
		{
			var hex = HatchScrubberLook.NormalizeColor(Ink);
			Color ink;
			if (hex == HatchScrubberLook.Accent)
			{
				// The ACCENT, as DATA: the OS accent shade AccentFillColorDefaultBrush uses for this element's theme (Light2
				// on dark, Dark1 on light), read from UISettings — a theme brush can't be resolved here, and the fade needs
				// the colour's own clear twin, which a XAML resource can't derive.
				var shade = ActualTheme == ElementTheme.Light ? UIColorType.AccentDark1 : UIColorType.AccentLight2;
				ink = new UISettings().GetColorValue(shade);
			}
			else
			{
				ink = Microsoft.UI.ColorHelper.FromArgb(0xFF, Hex(hex, 1), Hex(hex, 3), Hex(hex, 5));
			}
			// ABSOLUTE mapping: the fade spans the STRIP's depth, not each Path's bounds (a half-loaded strip's lines are
			// shorter — they must stay as bright as the edge's at the same height). Edge at the bottom, clear at the top.
			var brush = new LinearGradientBrush
			{
				MappingMode = BrushMappingMode.Absolute,
				StartPoint = new Point(0, Depth),
				EndPoint = new Point(0, 0),
				GradientStops =
				{
					new GradientStop { Offset = 0, Color = ink },
					new GradientStop { Offset = 1, Color = Microsoft.UI.ColorHelper.FromArgb(0, ink.R, ink.G, ink.B) },
				},
			};
			HatchScrubberLines.Stroke = brush;
			HatchScrubberCurrentLines.Stroke = brush;
		}

		private static byte Hex(string hex, int at) =>
			byte.Parse(hex.AsSpan(at, 2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);

		// ---- Segment tracking ----

		private void Attach()
		{
			Detach();
			if (IsLoaded && Segments is { } segments)
			{
				_attached = segments;
				_attached.CollectionChanged += OnSegmentsChanged;
				Watch();
			}
			QueueDraw();
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

		// A new loop / a remap / a live append (RebuildSegments = a Clear + one Add per frame): re-watch and redraw, once.
		private bool _rewatchQueued;

		private void OnSegmentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
		{
			_rewatchQueued = true;
			QueueDraw();
		}

		private void OnSegmentChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName is nameof(RadarFrameSegment.Fill) or nameof(RadarFrameSegment.IsReady)) QueueDraw();
		}

		private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e) => QueueDraw();

		// ⚠️ ONE redraw per dispatcher turn: a loading loop moves many frames at once (RefreshSegmentFill walks them all,
		// several times a second) and a Segments burst is N + 1 events — the DotMatrixScrubber lesson (2026-10-10/13).
		private bool _drawQueued;

		private void QueueDraw()
		{
			if (_drawQueued) return;
			_drawQueued = true;
			if (DispatcherQueue?.TryEnqueue(Draw) != true) Draw(); // no queue (shutting down): draw now
		}

		// COMPUTED GEOMETRY (the allowed exception — the Paths themselves are declared in XAML): the whole strip, from
		// HatchScrubberStrip, edge at the bottom (band y flipped).
		private void Draw()
		{
			_drawQueued = false;
			if (_rewatchQueued)
			{
				_rewatchQueued = false;
				Unwatch();
				Watch();
			}
			double length = HatchScrubberCanvas.ActualWidth;
			var segments = _attached;
			if (segments is null || segments.Count == 0 || length <= 0)
			{
				HatchScrubberLines.Data = null;
				HatchScrubberCurrentLines.Data = null;
				return;
			}
			var fills = new double[segments.Count];
			for (int i = 0; i < fills.Length; i++) fills[i] = segments[i].IsReady ? 1 : segments[i].Fill;
			int current = Math.Clamp((int)Math.Round(CurrentIndex), 0, fills.Length - 1);
			var (lines, onScreen) = HatchScrubberStrip.Build(length, Depth, Spacing, fills, current);
			HatchScrubberLines.Data = ToGeometry(lines);
			HatchScrubberCurrentLines.Data = ToGeometry(onScreen);
		}

		private PathGeometry ToGeometry(List<HatchSegment> segments)
		{
			var geometry = new PathGeometry();
			foreach (var s in segments)
			{
				var figure = new PathFigure { StartPoint = new Point(s.X1, Depth - s.Y1), IsClosed = false, IsFilled = false };
				figure.Segments.Add(new LineSegment { Point = new Point(s.X2, Depth - s.Y2) });
				geometry.Figures.Add(figure);
			}
			return geometry;
		}
	}
}
