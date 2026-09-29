using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Anvil.Controls.Primitives
{
	/// <summary>
	/// The short rule between tool groups on the tools tier (see MapControlsStripSeparator.xaml). Every part of
	/// its shape is a DP; <see cref="Brush"/> null keeps the theme default.
	/// </summary>
	public sealed partial class MapControlsStripSeparator : UserControl
	{
		public MapControlsStripSeparator()
		{
			InitializeComponent();
			ApplyShape();
		}

		/// <summary>Horizontal = a dash (Length wide); Vertical = a rule (Length tall).</summary>
		public Orientation Orientation
		{
			get => (Orientation)GetValue(OrientationProperty);
			set => SetValue(OrientationProperty, value);
		}

		public static readonly DependencyProperty OrientationProperty =
			DependencyProperty.Register(nameof(Orientation), typeof(Orientation), typeof(MapControlsStripSeparator),
				new PropertyMetadata(Orientation.Horizontal, OnShapeChanged));

		/// <summary>Along the bar, in DIPs.</summary>
		public double Length
		{
			get => (double)GetValue(LengthProperty);
			set => SetValue(LengthProperty, value);
		}

		public static readonly DependencyProperty LengthProperty =
			DependencyProperty.Register(nameof(Length), typeof(double), typeof(MapControlsStripSeparator),
				new PropertyMetadata(12.0, OnShapeChanged));

		/// <summary>Across the bar, in DIPs.</summary>
		public double Thickness
		{
			get => (double)GetValue(ThicknessProperty);
			set => SetValue(ThicknessProperty, value);
		}

		public static readonly DependencyProperty ThicknessProperty =
			DependencyProperty.Register(nameof(Thickness), typeof(double), typeof(MapControlsStripSeparator),
				new PropertyMetadata(2.0, OnShapeChanged));

		/// <summary>The bar's own corners (shadows UserControl.CornerRadius, which a UserControl doesn't draw).</summary>
		public new CornerRadius CornerRadius
		{
			get => (CornerRadius)GetValue(CornerRadiusProperty);
			set => SetValue(CornerRadiusProperty, value);
		}

		public static new readonly DependencyProperty CornerRadiusProperty =
			DependencyProperty.Register(nameof(CornerRadius), typeof(CornerRadius), typeof(MapControlsStripSeparator),
				new PropertyMetadata(new CornerRadius(1), OnShapeChanged));

		/// <summary>The bar's fill. Null = the theme default drawn in XAML.</summary>
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

		// ONE place every DP lands, every branch setting every value (the OverlayBar rule).
		private void ApplyShape()
		{
			bool across = Orientation == Orientation.Horizontal;
			double width = across ? Length : Thickness;
			double height = across ? Thickness : Length;
			foreach (var mark in new[] { DefaultMark, CustomMark })
			{
				mark.Width = width;
				mark.Height = height;
				mark.CornerRadius = CornerRadius;
			}
			CustomMark.Background = Brush;
			CustomMark.Visibility = Brush is null ? Visibility.Collapsed : Visibility.Visible;
			DefaultMark.Visibility = Brush is null ? Visibility.Visible : Visibility.Collapsed;
		}
	}
}
