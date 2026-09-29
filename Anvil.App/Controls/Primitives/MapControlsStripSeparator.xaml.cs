using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Anvil.Controls.Primitives
{
	/// <summary>
	/// The 3 × 3 dot grid between tool groups on the tools tier (see MapControlsStripSeparator.xaml). Dot size,
	/// spacing and fill are DPs; <see cref="Brush"/> null keeps the faint theme default.
	/// </summary>
	public sealed partial class MapControlsStripSeparator : UserControl
	{
		private readonly Ellipse[] _dots = new Ellipse[9];

		public MapControlsStripSeparator()
		{
			InitializeComponent();
			BuildDots();
			ApplyShape();
		}

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

		private static void OnShapeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
			((MapControlsStripSeparator)d).ApplyShape();

		// Nine Ellipses in the 3 × 3 grid, each wearing DotStyle (a STYLE is theme-safe; a brush read in C# isn't).
		private void BuildDots()
		{
			var style = (Style)Resources["DotStyle"];
			for (int i = 0; i < _dots.Length; i++)
			{
				var dot = new Ellipse { Style = style };
				Grid.SetRow(dot, i / 3);
				Grid.SetColumn(dot, i % 3);
				Dots.Children.Add(dot);
				_dots[i] = dot;
			}
		}

		// ONE place every DP lands, every branch setting every value (the OverlayBar rule).
		private void ApplyShape()
		{
			Dots.RowSpacing = DotGap;
			Dots.ColumnSpacing = DotGap;
			foreach (var dot in _dots)
			{
				if (dot is null)
				{
					return; // a DP set before BuildDots ran (XAML attribute on construction)
				}
				dot.Width = DotSize;
				dot.Height = DotSize;
				if (Brush is { } brush)
				{
					dot.Fill = brush;
				}
				else
				{
					dot.ClearValue(Shape.FillProperty); // back to DotStyle's theme brush
				}
			}
		}
	}
}
