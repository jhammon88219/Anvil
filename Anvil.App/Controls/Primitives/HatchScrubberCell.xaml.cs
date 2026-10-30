using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Anvil.Models;
using Point = Windows.Foundation.Point;

namespace Anvil.Controls.Primitives
{
	/// <summary>
	/// One hatch-scrubber frame (see HatchScrubberCell.xaml): its outline + its <see cref="HatchScrubberLadder"/> lines,
	/// rebuilt only when the level or the size changes.
	/// </summary>
	public sealed partial class HatchScrubberCell : UserControl
	{
		private int _level = -1;
		private double _w = -1, _h = -1;

		public HatchScrubberCell()
		{
			InitializeComponent();
		}

		/// <summary>The frame's progress (RadarFrameSegment.Fill: ½ download + ½ build).</summary>
		public double Fill
		{
			get => (double)GetValue(FillProperty);
			set => SetValue(FillProperty, value);
		}
		public static readonly DependencyProperty FillProperty =
			DependencyProperty.Register(nameof(Fill), typeof(double), typeof(HatchScrubberCell),
				new PropertyMetadata(0.0, (d, _) => ((HatchScrubberCell)d).Rebuild()));

		/// <summary>The frame is built and revealed (RadarFrameSegment.IsReady).</summary>
		public bool IsReady
		{
			get => (bool)GetValue(IsReadyProperty);
			set => SetValue(IsReadyProperty, value);
		}
		public static readonly DependencyProperty IsReadyProperty =
			DependencyProperty.Register(nameof(IsReady), typeof(bool), typeof(HatchScrubberCell),
				new PropertyMetadata(false, (d, _) => ((HatchScrubberCell)d).Rebuild()));

		private void OnSizeChanged(object sender, SizeChangedEventArgs e) => Rebuild();

		// COMPUTED GEOMETRY (the allowed exception — both Paths are declared in XAML). Laid out half a pixel in so the
		// 1 px strokes sit on whole pixels and the outline isn't clipped at the cell's edge.
		private void Rebuild()
		{
			if (HatchScrubberCellRoot is null) return;
			double w = HatchScrubberCellRoot.ActualWidth - 1, h = HatchScrubberCellRoot.ActualHeight - 1;
			int level = HatchScrubberLadder.Level(Fill, IsReady);
			bool sized = w != _w || h != _h;
			if (!sized && level == _level) return;
			_level = level;
			_w = w;
			_h = h;
			if (w <= 0 || h <= 0)
			{
				HatchScrubberCellOutline.Data = null;
				HatchScrubberCellLines.Data = null;
				return;
			}

			if (sized)
			{
				var outline = HatchScrubberGeometry.Outline(w, h);
				var figure = new PathFigure { StartPoint = At(outline[0].X, outline[0].Y), IsClosed = true, IsFilled = false };
				for (int i = 1; i < outline.Length; i++) figure.Segments.Add(new LineSegment { Point = At(outline[i].X, outline[i].Y) });
				var shape = new PathGeometry();
				shape.Figures.Add(figure);
				HatchScrubberCellOutline.Data = shape;
			}

			var lines = new PathGeometry();
			foreach (var s in HatchScrubberGeometry.Lines(w, h, level))
			{
				var figure = new PathFigure { StartPoint = At(s.X1, s.Y1), IsClosed = false, IsFilled = false };
				figure.Segments.Add(new LineSegment { Point = At(s.X2, s.Y2) });
				lines.Figures.Add(figure);
			}
			HatchScrubberCellLines.Data = lines;
		}

		private static Point At(double x, double y) => new(x + 0.5, y + 0.5);
	}
}
