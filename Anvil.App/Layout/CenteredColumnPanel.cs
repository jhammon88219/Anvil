using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Anvil.Layout
{
	/// <summary>
	/// Fills its slot, and lays each child out at most <see cref="ColumnWidth"/> wide, CENTRED in it. On a slot
	/// narrower than that, the child simply fills it. Used for the Anvil Atlas detail panes.
	///
	/// ⚠️ WHY NOT <c>MaxWidth</c> + Stretch on the child: WinUI centres a capped Stretch element by its DESIRED
	/// width, not by the width it's arranged at. A detail whose content only asks for ~490 (a short past event)
	/// was placed as if 490 wide, then drawn 760 wide — spilling right. This panel centres by the arranged
	/// width, whatever the content asks for.
	/// ⚠️ Domain-free, like <see cref="EqualCellsPanel"/>.
	/// </summary>
	public sealed partial class CenteredColumnPanel : Panel
	{
		/// <summary>The widest a child is laid out, in px.</summary>
		public double ColumnWidth
		{
			get => (double)GetValue(ColumnWidthProperty);
			set => SetValue(ColumnWidthProperty, value);
		}

		public static readonly DependencyProperty ColumnWidthProperty =
			DependencyProperty.Register(nameof(ColumnWidth), typeof(double), typeof(CenteredColumnPanel),
				new PropertyMetadata(double.PositiveInfinity, (d, _) => ((CenteredColumnPanel)d).InvalidateMeasure()));

		protected override Size MeasureOverride(Size availableSize)
		{
			var width = Math.Min(availableSize.Width, ColumnWidth);
			double desiredWidth = 0, desiredHeight = 0;

			foreach (var child in Children)
			{
				child.Measure(new Size(width, availableSize.Height));
				desiredWidth = Math.Max(desiredWidth, child.DesiredSize.Width);
				desiredHeight = Math.Max(desiredHeight, child.DesiredSize.Height);
			}

			// A finite slot is claimed whole (the centring happens in it); an infinite one gets the content's width.
			return new Size(double.IsInfinity(width) ? desiredWidth : width, desiredHeight);
		}

		protected override Size ArrangeOverride(Size finalSize)
		{
			var width = Math.Min(finalSize.Width, ColumnWidth);
			var x = (finalSize.Width - width) / 2;

			foreach (var child in Children)
			{
				child.Arrange(new Rect(x, 0, width, finalSize.Height));
			}

			return finalSize;
		}
	}
}
