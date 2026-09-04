using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Anvil.ViewModels;

namespace Anvil.Controls.Composites
{
	/// <summary>
	/// The map-tools strip that floats above the bottom OverlayBar (see the XAML header for the shape and
	/// the rules). This half owns one thing: the shell's OUTLINE, which is a rounded strip with a notch cut
	/// in its underside that traces the bar's pull-tab.
	/// </summary>
	/// <remarks>
	/// ⚠️ THE GEOMETRY IS BUILT IN CODE, not authored as a Data string, because the strip is content-sized:
	/// tools get added over time and the notch has to stay on the strip's midpoint through every width. A
	/// static path would pin the notch to whatever width the strip happened to have the day it was drawn.
	/// </remarks>
	public sealed partial class MapControlsStrip : UserControl
	{
		// ── The notch, derived from the tab it traces ────────────────────────────────────────────────
		// ⚠️ ONLY the CLEARANCE is ours. Everything else comes from Controls/Styles.xaml, where the tab's
		// own width / height / corner radius live precisely so this control can read the same numbers the
		// tab is drawn with. Do not copy the tab's dimensions here.
		private const double NotchClearance = 6;   // uniform gap between the tab's edge and the cut

		// The strip's own shape. Local because nothing else has an opinion about them.
		private const double StripHeight = 46;
		private const double ShellRadius = 9;

		public MapControlsStrip()
		{
			InitializeComponent();
			Root.Height = StripHeight;
			// The notch column reserves the tab's footprint inside the tool grid, so a tool can never be
			// laid out over the hole. Same source as the geometry below — they must agree.
			NotchColumn.Width = new GridLength(TabWidth + (NotchClearance * 2));
		}

		/// <summary>The coordinator view model; bound from the host.</summary>
		public MapViewModel ViewModel
		{
			get => (MapViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(MapControlsStrip), new PropertyMetadata(null));

		// ── The tab's numbers, read from the app-wide dictionary ─────────────────────────────────────
		// The fallbacks match the values in Controls/Styles.xaml: a mis-keyed lookup then draws a slightly
		// wrong notch rather than a degenerate one, which is far easier to spot and to fix.
		private static double TabWidth => SharedSize("OverlayBarTabWidth", 96);
		private static double TabHeight => SharedSize("OverlayBarTabHeight", 28);
		private static double TabRadius => SharedSize("OverlayBarTabRadius", 7);

		private static double SharedSize(string key, double fallback) =>
			Application.Current.Resources.TryGetValue(key, out var v) && v is double d ? d : fallback;

		/// <summary>
		/// How far the strip must hang BELOW the OverlayBar's top edge for the tab to reach into the notch,
		/// as a negative bottom margin. The host applies it; it is exposed here because it is the same
		/// arithmetic as the notch depth and the two must not disagree.
		/// </summary>
		/// <remarks>
		/// The OverlayBar's top edge IS the tab's top edge (the tab is its first row), so stacking the strip
		/// above it would leave the two merely touching. Pulling the strip down by <c>tab height − the gap
		/// we want above the bar</c> is what makes the tab rise through the cut.
		/// </remarks>
		public static double BarOverlap => TabHeight - GapAboveBar;

		/// <summary>Clear air between the strip's underside and the bar's top edge, either side of the tab.</summary>
		private const double GapAboveBar = 10;

		// How deep the cut goes, measured from the strip's bottom edge: the part of the tab that rises past
		// that edge, plus the clearance.
		private static double NotchDepth => (TabHeight - GapAboveBar) + NotchClearance;

		private void OnRootSizeChanged(object sender, SizeChangedEventArgs e) =>
			Shell.Data = BuildShellGeometry(e.NewSize.Width, e.NewSize.Height);

		private void OnToolsLayoutUpdated(object? sender, object e) => EqualiseSides();

		/// <summary>
		/// Push both side columns out to the wider one's width, so the notch this grid RESERVES lands on
		/// the notch the Path DRAWS.
		/// </summary>
		/// <remarks>
		/// ⚠️⚠️ THIS IS CORRECTNESS, NOT COSMETICS. The cut is drawn at the shell's midpoint; the middle
		/// column reserves the room for it. Those two coincide ONLY when the side columns are equal —
		/// unequal sides slide the reserved gap off the drawn cut and a tool ends up sitting on the pull-tab
		/// (measured: the isolation combo did exactly that).
		/// ⚠️ Star columns cannot do this job here, which is what the first attempt got wrong. `[*, notch, *]`
		/// equalises only when the grid has spare width to divide; this strip is CONTENT-sized, so the stars
		/// just took their content's width and the sides came out uneven.
		/// ⚠️ THE CHANGE GUARD IS LOAD-BEARING: this runs on every layout pass, and writing MinWidth
		/// unconditionally would schedule another pass forever. Only a real change is written, and widening
		/// a column cannot change either panel's DesiredSize, so it settles in one extra pass.
		/// </remarks>
		private void EqualiseSides()
		{
			double want = Math.Max(LeftTools.DesiredSize.Width, RightTools.DesiredSize.Width);
			if (want <= 0 || Math.Abs(want - LeftColumn.MinWidth) < 0.5)
			{
				return;
			}

			LeftColumn.MinWidth = want;
			RightColumn.MinWidth = want;
		}

		/// <summary>
		/// A rounded rectangle with a notch cut into the bottom edge, centred. The notch is the tab's
		/// rectangle grown by <see cref="NotchClearance"/> on every side, so its arcs are CONCENTRIC with
		/// the tab's own corners and the gap between the two reads as uniform.
		/// </summary>
		private static Geometry BuildShellGeometry(double w, double h)
		{
			// Half a stroke in from the edge, or the 1px outline is drawn half outside the control and the
			// left/right hairlines look thinner than the top.
			const double inset = 0.5;
			double r = ShellRadius;
			double nw = TabWidth + (NotchClearance * 2);
			double nr = TabRadius + NotchClearance;   // concentric with the tab's corner
			double depth = NotchDepth;

			double left = inset, right = w - inset, top = inset, bottom = h - inset;
			double nx1 = (w - nw) / 2, nx2 = (w + nw) / 2, ny = bottom - depth;

			// A degenerate size (first measure, or a strip narrower than its own notch) would produce a
			// self-crossing path; draw nothing rather than something wrong.
			if (w <= nw + (r * 2) || h <= depth + r)
			{
				return new PathGeometry();
			}

			var figure = new PathFigure { StartPoint = new Point(left + r, top), IsClosed = true, IsFilled = true };

			void Line(double x, double y) => figure.Segments.Add(new LineSegment { Point = new Point(x, y) });
			void Arc(double x, double y, double radius, SweepDirection sweep) =>
				figure.Segments.Add(new ArcSegment
				{
					Point = new Point(x, y),
					Size = new Size(radius, radius),
					SweepDirection = sweep,
					RotationAngle = 0,
					IsLargeArc = false
				});

			// Clockwise: across the top, down the right, then RIGHT-TO-LEFT along the bottom — which is
			// where the notch interrupts it.
			Line(right - r, top);
			Arc(right, top + r, r, SweepDirection.Clockwise);
			Line(right, bottom - r);
			Arc(right - r, bottom, r, SweepDirection.Clockwise);

			// The notch. Its two top corners are INSIDE corners of the shape, so they sweep the opposite
			// way to the outer four.
			Line(nx2, bottom);
			Line(nx2, ny + nr);
			Arc(nx2 - nr, ny, nr, SweepDirection.Counterclockwise);
			Line(nx1 + nr, ny);
			Arc(nx1, ny + nr, nr, SweepDirection.Counterclockwise);
			Line(nx1, bottom);

			Line(left + r, bottom);
			Arc(left, bottom - r, r, SweepDirection.Clockwise);
			Line(left, top + r);
			Arc(left + r, top, r, SweepDirection.Clockwise);

			var geometry = new PathGeometry();
			geometry.Figures.Add(figure);
			return geometry;
		}

		// Reset north — animate bearing + pitch back to 0. Fire-and-forget through IMapService, the same
		// seam the Settings window's Map tab uses.
		private void OnResetNorthClick(object sender, RoutedEventArgs e) =>
			_ = ViewModel?.ResetOrientationAsync();

		// Fit to view — frame the effective region (isolated state, else CONUS).
		private void OnFitToViewClick(object sender, RoutedEventArgs e) =>
			_ = ViewModel?.FitToViewAsync();
	}
}
