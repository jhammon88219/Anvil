using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Anvil.Layout
{
	/// <summary>
	/// Lays its children out left-to-right, starting a new line whenever the next child wouldn't fit —
	/// WinUI 3 ships no WrapPanel, and a horizontal StackPanel just runs off the edge. Used as the ItemsPanel
	/// for the Radar Atlas's active-filter chips, which sit in a 360 px column where three or four chips of
	/// unpredictable width have to go somewhere.
	///
	/// ⚠️ Domain-free, like <see cref="EqualCellsPanel"/>: it knows nothing about chips or filters, only
	/// "flow these children and wrap". Each child keeps its own desired size — this panel never stretches
	/// one, so a chip is exactly as wide as its label.
	/// </summary>
	public sealed partial class WrapPanel : Panel
	{
		/// <summary>Gap between children on a line.</summary>
		public double HorizontalSpacing
		{
			get => (double)GetValue(HorizontalSpacingProperty);
			set => SetValue(HorizontalSpacingProperty, value);
		}

		public static readonly DependencyProperty HorizontalSpacingProperty =
			DependencyProperty.Register(nameof(HorizontalSpacing), typeof(double), typeof(WrapPanel),
				new PropertyMetadata(0d, OnLayoutPropertyChanged));

		/// <summary>Gap between lines.</summary>
		public double VerticalSpacing
		{
			get => (double)GetValue(VerticalSpacingProperty);
			set => SetValue(VerticalSpacingProperty, value);
		}

		public static readonly DependencyProperty VerticalSpacingProperty =
			DependencyProperty.Register(nameof(VerticalSpacing), typeof(double), typeof(WrapPanel),
				new PropertyMetadata(0d, OnLayoutPropertyChanged));

		private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
			((WrapPanel)d).InvalidateMeasure();

		protected override Size MeasureOverride(Size availableSize)
		{
			// An infinite width (inside a StackPanel, say) means "never wrap" — one line, measured to content.
			var limit = double.IsInfinity(availableSize.Width) ? double.MaxValue : availableSize.Width;
			double lineWidth = 0, lineHeight = 0, totalWidth = 0, totalHeight = 0;

			foreach (var child in Children)
			{
				child.Measure(new Size(limit, double.PositiveInfinity));
				var size = child.DesiredSize;
				var advance = lineWidth > 0 ? HorizontalSpacing + size.Width : size.Width;

				if (lineWidth > 0 && lineWidth + advance > limit)
				{
					totalWidth = System.Math.Max(totalWidth, lineWidth);
					totalHeight += (totalHeight > 0 ? VerticalSpacing : 0) + lineHeight;
					lineWidth = size.Width;
					lineHeight = size.Height;
					continue;
				}

				lineWidth += advance;
				lineHeight = System.Math.Max(lineHeight, size.Height);
			}

			totalWidth = System.Math.Max(totalWidth, lineWidth);
			totalHeight += (totalHeight > 0 ? VerticalSpacing : 0) + lineHeight;
			return new Size(totalWidth, totalHeight);
		}

		protected override Size ArrangeOverride(Size finalSize)
		{
			double x = 0, y = 0, lineHeight = 0;

			foreach (var child in Children)
			{
				var size = child.DesiredSize;
				var advance = x > 0 ? HorizontalSpacing + size.Width : size.Width;

				// ⚠️ Same wrap test as MeasureOverride, or a child measured onto line 2 gets arranged onto
				// line 1 and overflows the panel.
				if (x > 0 && x + advance > finalSize.Width)
				{
					x = 0;
					y += lineHeight + VerticalSpacing;
					lineHeight = 0;
					advance = size.Width;
				}

				child.Arrange(new Rect(x + advance - size.Width, y, size.Width, size.Height));
				x += advance;
				lineHeight = System.Math.Max(lineHeight, size.Height);
			}

			return finalSize;
		}
	}
}
