using System;
using Windows.Foundation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Anvil.Controls.Primitives
{
	/// <summary>
	/// One row of children in EQUAL columns — what a Grid of N star columns does, for an ItemsControl whose
	/// item count is data (a Grid's columns can't be bound, and building them in code is UI defined at
	/// runtime). Layout only: it creates nothing, it measures and arranges the children the template made.
	/// </summary>
	/// <remarks>
	/// <code>
	///   finite width   ┌─────┬─────┬─────┬─────┐   each column = width / N
	///   infinite width ┌───┬───┬───┬───┐           each column = the widest child (a Left-aligned star Grid)
	/// </code>
	/// Used by <see cref="SegmentedPicker"/> (its segments) and <see cref="LoopLoadProgressBar"/> (its cells,
	/// with <see cref="Spacing"/> between them).
	/// </remarks>
	public sealed class EqualColumnsPanel : Panel
	{
		/// <summary>Gap between neighbouring columns, px.</summary>
		public double Spacing
		{
			get => (double)GetValue(SpacingProperty);
			set => SetValue(SpacingProperty, value);
		}

		public static readonly DependencyProperty SpacingProperty =
			DependencyProperty.Register(nameof(Spacing), typeof(double), typeof(EqualColumnsPanel),
				new PropertyMetadata(0.0, (d, _) => ((EqualColumnsPanel)d).InvalidateMeasure()));

		protected override Size MeasureOverride(Size availableSize)
		{
			var n = Children.Count;
			if (n == 0)
			{
				return new Size(0, 0);
			}

			var gaps = Spacing * (n - 1);
			var finite = !double.IsInfinity(availableSize.Width);
			var slot = finite ? Math.Max(0, (availableSize.Width - gaps) / n) : double.PositiveInfinity;

			double widest = 0, tallest = 0;
			foreach (var child in Children)
			{
				child.Measure(new Size(slot, availableSize.Height));
				widest = Math.Max(widest, child.DesiredSize.Width);
				tallest = Math.Max(tallest, child.DesiredSize.Height);
			}

			return new Size(finite ? availableSize.Width : widest * n + gaps, tallest);
		}

		protected override Size ArrangeOverride(Size finalSize)
		{
			var n = Children.Count;
			if (n == 0)
			{
				return finalSize;
			}

			var slot = Math.Max(0, (finalSize.Width - Spacing * (n - 1)) / n);
			for (var i = 0; i < n; i++)
			{
				Children[i].Arrange(new Rect(i * (slot + Spacing), 0, slot, finalSize.Height));
			}

			return finalSize;
		}
	}
}
