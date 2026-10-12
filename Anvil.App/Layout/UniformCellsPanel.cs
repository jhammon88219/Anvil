using System;
using Windows.Foundation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Anvil.Layout
{
	/// <summary>
	/// Children in a grid of <see cref="Columns"/> equal columns and as many equal rows as they need, filling the
	/// panel — what a Grid of star rows and columns does, for an ItemsControl whose cell count is data (a Grid's
	/// rows and columns can't be bound, and building them in code is UI defined at runtime). Layout only.
	/// </summary>
	/// <remarks>Used by DateButtonPicker's three pages: days (7 across), months (3), years (4). Domain-free, like
	/// <see cref="EqualCellsPanel"/>.</remarks>
	public sealed partial class UniformCellsPanel : Panel
	{
		/// <summary>Cells across.</summary>
		public int Columns
		{
			get => (int)GetValue(ColumnsProperty);
			set => SetValue(ColumnsProperty, value);
		}

		public static readonly DependencyProperty ColumnsProperty =
			DependencyProperty.Register(nameof(Columns), typeof(int), typeof(UniformCellsPanel),
				new PropertyMetadata(1, (d, _) => ((UniformCellsPanel)d).InvalidateMeasure()));

		private int Cols => Math.Max(1, Columns);
		private int Rows => Math.Max(1, (Children.Count + Cols - 1) / Cols);

		protected override Size MeasureOverride(Size availableSize)
		{
			var cell = new Size(
				double.IsInfinity(availableSize.Width) ? double.PositiveInfinity : availableSize.Width / Cols,
				double.IsInfinity(availableSize.Height) ? double.PositiveInfinity : availableSize.Height / Rows);
			double widest = 0, tallest = 0;
			foreach (var child in Children)
			{
				child.Measure(cell);
				widest = Math.Max(widest, child.DesiredSize.Width);
				tallest = Math.Max(tallest, child.DesiredSize.Height);
			}
			return new Size(
				double.IsInfinity(availableSize.Width) ? widest * Cols : availableSize.Width,
				double.IsInfinity(availableSize.Height) ? tallest * Rows : availableSize.Height);
		}

		protected override Size ArrangeOverride(Size finalSize)
		{
			var w = finalSize.Width / Cols;
			var h = finalSize.Height / Rows;
			for (var i = 0; i < Children.Count; i++)
			{
				Children[i].Arrange(new Rect(i % Cols * w, i / Cols * h, w, h));
			}
			return finalSize;
		}
	}
}
