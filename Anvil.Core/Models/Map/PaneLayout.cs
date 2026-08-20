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
		/// (which positions the panes and draws the groove) and the XAML watermark grid (which has to
		/// land its labels in the panes' real corners) — passed to JS in
		/// <c>IMapService.SetPaneLayoutAsync</c> rather than written down twice.
		/// </summary>
		public const int GutterPx = 5;

		/// <summary>How many panes the app ever has view models for (the largest layout).</summary>
		public const int MaxPanes = 4;

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
