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
		private Ellipse[] _dots = Array.Empty<Ellipse>();
		private int _builtColumns = -1;
		private int _builtRows = -1;

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

		// Rebuild the grid only when the COUNT changes (a fill separator re-lays out often); size/gap/brush are
		// re-applied every time. ONE place every DP lands (the OverlayBar rule).
		private void ApplyShape()
		{
			if (Dots is null)
			{
				return; // a DP set before InitializeComponent
			}
			int columns = EffectiveColumns();
			int rows = Math.Max(1, Rows);
			if (columns != _builtColumns || rows != _builtRows)
			{
				BuildDots(columns, rows);
			}
			Dots.RowSpacing = DotGap;
			Dots.ColumnSpacing = DotGap;
			foreach (var dot in _dots)
			{
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

		// columns × rows Ellipses, each wearing DotStyle (a STYLE is theme-safe; a brush read in C# isn't).
		private void BuildDots(int columns, int rows)
		{
			var style = (Style)Resources["DotStyle"];
			Dots.Children.Clear();
			Dots.RowDefinitions.Clear();
			Dots.ColumnDefinitions.Clear();
			for (int r = 0; r < rows; r++)
			{
				Dots.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			}
			for (int c = 0; c < columns; c++)
			{
				Dots.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
			}
			_dots = new Ellipse[columns * rows];
			for (int i = 0; i < _dots.Length; i++)
			{
				var dot = new Ellipse { Style = style };
				Grid.SetRow(dot, i / columns);
				Grid.SetColumn(dot, i % columns);
				Dots.Children.Add(dot);
				_dots[i] = dot;
			}
			_builtColumns = columns;
			_builtRows = rows;
		}
	}
}
