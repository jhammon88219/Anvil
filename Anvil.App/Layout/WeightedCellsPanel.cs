using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Anvil.Layout
{
	/// <summary>
	/// Lays its children out left-to-right in ONE row, each as wide as its share of the total <see cref="WeightProperty">Weight</see>
	/// — what a Grid of weighted star columns does, for an ItemsControl (a Grid's columns can't be bound, and building
	/// them in code is UI defined at runtime). Used for the Radar Atlas's scan-pattern bar, one cell per VCP sized by
	/// its share of the sampled hours.
	///
	/// ⚠️ Domain-free, like <see cref="EqualCellsPanel"/>. The weight is an attached property set on the item
	/// template's ROOT (<c>layout:WeightedCellsPanel.Weight="{x:Bind Share}"</c>); under an ItemsControl that root
	/// sits inside a ContentPresenter, so the panel reads the property from the child OR its first visual child.
	/// </summary>
	public sealed partial class WeightedCellsPanel : Panel
	{
		/// <summary>A child's share of the row (any non-negative scale; 0 = no width).</summary>
		public static readonly DependencyProperty WeightProperty =
			DependencyProperty.RegisterAttached("Weight", typeof(double), typeof(WeightedCellsPanel),
				new PropertyMetadata(1d, OnWeightChanged));

		public static double GetWeight(DependencyObject element) => (double)element.GetValue(WeightProperty);
		public static void SetWeight(DependencyObject element, double value) => element.SetValue(WeightProperty, value);

		/// <summary>Gap between neighbouring cells, px.</summary>
		public double Spacing
		{
			get => (double)GetValue(SpacingProperty);
			set => SetValue(SpacingProperty, value);
		}

		public static readonly DependencyProperty SpacingProperty =
			DependencyProperty.Register(nameof(Spacing), typeof(double), typeof(WeightedCellsPanel),
				new PropertyMetadata(0d, (d, _) => ((WeightedCellsPanel)d).InvalidateMeasure()));

		// A weight changed on some element: re-arrange the panel it sits in (it may be one ContentPresenter down).
		private static void OnWeightChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
		{
			for (var p = d as DependencyObject; p is not null; p = VisualTreeHelper.GetParent(p))
			{
				if (p is WeightedCellsPanel panel)
				{
					panel.InvalidateArrange();
					return;
				}
			}
		}

		// The child's weight: on the child itself, else on the template root inside its ContentPresenter.
		private static double WeightOf(UIElement child)
		{
			if (child.ReadLocalValue(WeightProperty) != DependencyProperty.UnsetValue)
			{
				return Math.Max(0, GetWeight(child));
			}
			if (VisualTreeHelper.GetChildrenCount(child) > 0 && VisualTreeHelper.GetChild(child, 0) is DependencyObject root)
			{
				return Math.Max(0, GetWeight(root));
			}
			return Math.Max(0, GetWeight(child));
		}

		protected override Size MeasureOverride(Size availableSize)
		{
			double height = 0;
			foreach (var child in Children)
			{
				child.Measure(new Size(availableSize.Width, availableSize.Height));
				height = Math.Max(height, child.DesiredSize.Height);
			}
			return new Size(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, height);
		}

		protected override Size ArrangeOverride(Size finalSize)
		{
			var n = Children.Count;
			if (n == 0) return finalSize;
			double total = 0;
			var weights = new double[n];
			for (var i = 0; i < n; i++)
			{
				weights[i] = WeightOf(Children[i]);
				total += weights[i];
			}
			var free = Math.Max(0, finalSize.Width - Spacing * (n - 1));
			double x = 0;
			for (var i = 0; i < n; i++)
			{
				var w = total > 0 ? free * weights[i] / total : 0;
				Children[i].Arrange(new Rect(x, 0, w, finalSize.Height));
				x += w + Spacing;
			}
			return finalSize;
		}
	}
}
