namespace Anvil.Models
{
	/// <summary>
	/// How the map band is divided into panes. A pane is a PRODUCT VIEW of one site: every pane shares
	/// the site, the camera and the time cursor, and differs only in which radar moment it draws.
	///
	/// <para>Two-pane is deliberately SIDE BY SIDE only — there is no stacked variant. Weather is read
	/// left-to-right across a storm, and a stacked pair halves the vertical extent that matters most on
	/// a wide monitor.</para>
	///
	/// <para>The enum is the identifier; <see cref="PaneLayoutInfo"/> turns it into the grid the page
	/// and the bar's chip cluster both lay themselves out from, so "how many panes and how are they
	/// arranged" is answered in exactly one place.</para>
	/// </summary>
	public enum PaneLayout
	{
		/// <summary>One pane filling the map band (the launch layout).</summary>
		Single,

		/// <summary>Two panes side by side.</summary>
		TwoAcross,

		/// <summary>Four panes in a 2x2.</summary>
		Quad,
	}

	/// <summary>Grid geometry for a <see cref="PaneLayout"/>.</summary>
	public static class PaneLayoutInfo
	{
		/// <summary>
		/// Width of the groove between panes, in CSS/effective pixels. ONE constant, shared by the page
		/// (which positions the panes and draws the groove) and MainWindow's watermark overlay grid (whose
		/// cells have to match the panes' real rects) — passed to JS in
		/// <c>IMapService.SetPaneLayoutAsync</c> rather than written down twice.
		/// </summary>
		public const int GutterPx = 5;

		/// <summary>How many panes the app ever has view models for (the largest layout).</summary>
		public const int MaxPanes = 4;

		/// <summary>
		/// Where pane <paramref name="index"/> sits in a 3x3 <c>[*, gutter, *]</c> overlay grid laid over
		/// the same rect as the page's panes — the placement MainWindow's pane watermarks use. Rows and
		/// columns 0 and 2 are the panes; row/column 1 is the groove.
		///
		/// <para>⚠️ Pane 0 is the MAIN pane and sits BOTTOM-LEFT, so a quad reads
		/// <c>0=bottom-left, 1=bottom-right, 2=top-left, 3=top-right</c> — a vertical mirror of reading
		/// order. This MIRRORS <c>paneRects()</c> in map.js, which computes the real pane geometry; the two
		/// are the same rule expressed on either side of the WebView boundary, so a change to the pane
		/// arrangement has to be made in BOTH.</para>
		/// </summary>
		public static (int Row, int Column, int RowSpan, int ColumnSpan) CellOf(this PaneLayout layout, int index)
		{
			// A pane the layout doesn't show is parked on the whole grid; it is collapsed anyway.
			if (index < 0 || index >= layout.PaneCount())
			{
				return (0, 0, 3, 3);
			}

			return layout switch
			{
				// One pane covering everything: span the grooves too.
				PaneLayout.Single => (0, 0, 3, 3),
				// Two side by side: full height, one column each.
				PaneLayout.TwoAcross => (0, index == 0 ? 0 : 2, 3, 1),
				// Quad: bottom row first (panes 0/1), then the top row (panes 2/3).
				_ => (index < 2 ? 2 : 0, index % 2 == 0 ? 0 : 2, 1, 1),
			};
		}

		/// <summary>Columns in the pane grid.</summary>
		public static int Columns(this PaneLayout layout) => layout switch
		{
			PaneLayout.TwoAcross => 2,
			PaneLayout.Quad => 2,
			_ => 1,
		};

		/// <summary>Rows in the pane grid.</summary>
		public static int Rows(this PaneLayout layout) => layout == PaneLayout.Quad ? 2 : 1;

		/// <summary>How many panes the layout shows.</summary>
		public static int PaneCount(this PaneLayout layout) => layout.Columns() * layout.Rows();
	}
}
