using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Anvil.Layout
{
	/// <summary>
	/// Lays its children out left-to-right as EQUAL cells that tile the available width edge-to-edge — WinUI 3
	/// has no built-in UniformGrid, and a horizontal StackPanel sizes to content. Used as the ItemsPanel for
	/// every segmented strip in the app (the radar scrubber, and one per product row in the pipeline console),
	/// so N cells always split the track evenly and restretch on resize. Height matches the tallest child (the
	/// cells are uniform, so any of them).
	///
	/// ⚠️ Domain-free on purpose: it knows nothing about frames, products or readiness — only "divide this
	/// width into N cells". WHICH cells are lit is entirely view-model state (see
	/// <c>RadarViewModel.RefreshSegmentReadiness</c> and <c>docs/radar-loop-flow.md</c>).
	/// </summary>
	public sealed partial class EqualCellsPanel : Panel
	{
		/// <summary>
		/// The left edge of cell <paramref name="index"/> (0-based; pass <c>count</c> for the strip's right
		/// edge). Edges are ROUNDED rather than each cell being a fractional <c>width/count</c> wide, so
		/// consecutive cells share an exact pixel boundary — cumulative fractional widths would otherwise
		/// drift and leave seams between cells.
		/// <para>⚠️ <paramref name="index"/> is a <c>double</c> because the frame indices the playheads pass
		/// in are (<c>RadarViewModel.CurrentFrameIndex</c> / <c>PipelineConsoleViewModel.CurrentIndex</c>).
		/// <see cref="ArrangeOverride"/> passes a whole number; both cases go through the same
		/// arithmetic.</para>
		/// </summary>
		public static double CellEdge(double totalWidth, int count, double index) =>
			count <= 0 ? 0 : System.Math.Round(index * (totalWidth / count));

		/// <summary>
		/// The centre of cell <paramref name="index"/>. THE seam-free geometry a host needs to line something
		/// up with a cell — the scrubber playheads position themselves with this, so the rounding rule lives
		/// here (with the code that arranges the cells) instead of being re-derived at each call site.
		/// </summary>
		public static double CellCenter(double totalWidth, int count, double index) =>
			(CellEdge(totalWidth, count, index) + CellEdge(totalWidth, count, index + 1)) / 2;

		protected override Size MeasureOverride(Size availableSize)
		{
			var n = Children.Count;
			var cellWidth = (n > 0 && !double.IsInfinity(availableSize.Width)) ? availableSize.Width / n : 0;
			double height = 0;
			foreach (var child in Children)
			{
				child.Measure(new Size(cellWidth, availableSize.Height));
				if (child.DesiredSize.Height > height) height = child.DesiredSize.Height;
			}
			var width = double.IsInfinity(availableSize.Width) ? cellWidth * n : availableSize.Width;
			return new Size(width, height);
		}

		protected override Size ArrangeOverride(Size finalSize)
		{
			var n = Children.Count;
			if (n == 0) return finalSize;
			for (var i = 0; i < n; i++)
			{
				var x0 = CellEdge(finalSize.Width, n, i);
				var x1 = CellEdge(finalSize.Width, n, i + 1);
				Children[i].Arrange(new Rect(x0, 0, x1 - x0, finalSize.Height));
			}
			return finalSize;
		}
	}
}
