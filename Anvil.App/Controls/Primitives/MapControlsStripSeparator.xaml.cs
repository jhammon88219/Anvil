using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Anvil.Controls.Primitives
{
	/// <summary>
	/// The dot grid between tool groups on the tools tier (see MapControlsStripSeparator.xaml): <see cref="Columns"/>
	/// × <see cref="Rows"/> dots, or — with <see cref="FillWidth"/> — as many columns as its (host-set) Width holds.
	/// Dot size, spacing and fill are DPs; <see cref="Brush"/> null keeps the faint theme default.
	/// </summary>
	public sealed partial class MapControlsStripSeparator : UserControl
	{
		private (int Columns, int Rows, double Size, double Gap) _builtShape = (-1, -1, 0, 0);

		public MapControlsStripSeparator()
		{
			InitializeComponent();
			SizeChanged += (_, _) => { if (FillWidth) ApplyShape(); };
			ApplyShape();
		}

		/// <summary>Dots across (ignored while <see cref="FillWidth"/> is on).</summary>
		public int Columns
		{
			get => (int)GetValue(ColumnsProperty);
			set => SetValue(ColumnsProperty, value);
		}

		public static readonly DependencyProperty ColumnsProperty =
			DependencyProperty.Register(nameof(Columns), typeof(int), typeof(MapControlsStripSeparator),
				new PropertyMetadata(3, OnShapeChanged));

		/// <summary>Dots down.</summary>
		public int Rows
		{
			get => (int)GetValue(RowsProperty);
			set => SetValue(RowsProperty, value);
		}

		public static readonly DependencyProperty RowsProperty =
			DependencyProperty.Register(nameof(Rows), typeof(int), typeof(MapControlsStripSeparator),
				new PropertyMetadata(3, OnShapeChanged));

		/// <summary>
		/// Fill the control's WIDTH with as many columns as fit at the dot pitch (DotSize + DotGap), centred. The host
		/// sets Width — the tools tier's picker separator stretches to whatever gap the site picker leaves.
		/// </summary>
		public bool FillWidth
		{
			get => (bool)GetValue(FillWidthProperty);
			set => SetValue(FillWidthProperty, value);
		}

		public static readonly DependencyProperty FillWidthProperty =
			DependencyProperty.Register(nameof(FillWidth), typeof(bool), typeof(MapControlsStripSeparator),
				new PropertyMetadata(false, OnShapeChanged));

		/// <summary>Each dot's diameter, in DIPs.</summary>
		public double DotSize
		{
			get => (double)GetValue(DotSizeProperty);
			set => SetValue(DotSizeProperty, value);
		}

		public static readonly DependencyProperty DotSizeProperty =
			DependencyProperty.Register(nameof(DotSize), typeof(double), typeof(MapControlsStripSeparator),
				new PropertyMetadata(1.5, OnShapeChanged));

		/// <summary>The space between neighbouring dots, in DIPs (both directions).</summary>
		public double DotGap
		{
			get => (double)GetValue(DotGapProperty);
			set => SetValue(DotGapProperty, value);
		}

		public static readonly DependencyProperty DotGapProperty =
			DependencyProperty.Register(nameof(DotGap), typeof(double), typeof(MapControlsStripSeparator),
				new PropertyMetadata(2.5, OnShapeChanged));

		/// <summary>The dots' fill. Null = the theme default (DotStyle in XAML).</summary>
		public Brush? Brush
		{
			get => (Brush?)GetValue(BrushProperty);
			set => SetValue(BrushProperty, value);
		}

		public static readonly DependencyProperty BrushProperty =
			DependencyProperty.Register(nameof(Brush), typeof(Brush), typeof(MapControlsStripSeparator),
				new PropertyMetadata(null, OnShapeChanged));

		/// <summary>The width <paramref name="columns"/> dots take at the default size and gap — what a host needs
		/// to keep a FillWidth separator from dropping below a given count.</summary>
		public double WidthFor(int columns) => columns * DotSize + Math.Max(0, columns - 1) * DotGap;

		/// <summary>A host's measured fit: set Width to <paramref name="width"/>, never below
		/// <paramref name="minColumns"/> dots, and not at all for a sub-pixel change — so a host calling it from
		/// layout settles at once.</summary>
		public void SetFillWidth(double width, int minColumns)
		{
			var w = Math.Max(WidthFor(minColumns), width);
			if (double.IsNaN(Width) || Math.Abs(w - Width) > 0.5)
			{
				Width = w;
			}
		}

		private static void OnShapeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
			((MapControlsStripSeparator)d).ApplyShape();

		// Columns in force: the DP, or — filling — what the laid-out width holds (at least 1).
		private int EffectiveColumns()
		{
			if (!FillWidth)
			{
				return Math.Max(1, Columns);
			}
			var pitch = DotSize + DotGap;
			var width = double.IsNaN(Width) ? ActualWidth : Width;
			return pitch <= 0 ? 1 : Math.Max(1, (int)Math.Floor((width + DotGap) / pitch));
		}

		// Re-compute the path's circles only when the SHAPE changes (a fill separator re-lays out often); the brush is
		// re-applied every time. ONE place every DP lands (the OverlayBar rule).
		private void ApplyShape()
		{
			if (Dots is null)
			{
				return; // a DP set before InitializeComponent
			}
			var shape = (EffectiveColumns(), Math.Max(1, Rows), DotSize, DotGap);
			if (shape != _builtShape)
			{
				Dots.Data = DotGeometry(shape.Item1, shape.Item2, DotSize, DotGap);
				_builtShape = shape;
			}
			if (Brush is { } brush)
			{
				Dots.Fill = brush;
			}
			else
			{
				Dots.ClearValue(Shape.FillProperty); // back to DotStyle's theme brush
			}
		}

		// columns × rows circles at the pitch, from (0,0) — the path's own size is the grid's.
		private static GeometryGroup DotGeometry(int columns, int rows, double size, double gap)
		{
			var group = new GeometryGroup();
			var r = size / 2;
			var pitch = size + gap;
			for (int row = 0; row < rows; row++)
			{
				for (int c = 0; c < columns; c++)
				{
					group.Children.Add(new EllipseGeometry
					{
						Center = new global::Windows.Foundation.Point(c * pitch + r, row * pitch + r),
						RadiusX = r,
						RadiusY = r,
					});
				}
			}
			return group;
		}
	}
}
