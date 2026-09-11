using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Anvil.Controls.Primitives
{
	/// <summary>
	/// One pull-tab: the flat-bottomed lozenge that toggles the surface it is attached to (see the XAML
	/// header for the shape and the rules). It is the ONE definition of that tab, shared by
	/// <see cref="OverlayBar"/> - which draws one for itself - and by <c>MainWindow</c>'s bottom rail,
	/// which draws two side by side for the two tiers stacked below it.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The knobs are geometry (<see cref="Edge"/>, <see cref="ShowLabel"/>), wording
	/// (<see cref="Target"/>) and face (<see cref="Raised"/>). <see cref="ApplyChrome"/> applies the first
	/// two together and every branch sets every value, for the same reason
	/// <see cref="OverlayBar.ApplyChrome"/> does: the corner radius, the open border side, the lap margin
	/// and the padding are not independent of each other.
	/// </para>
	/// <para>
	/// ⚠️ The tab does NOT own the state it toggles. <see cref="IsShown"/> is two-way and the host owns
	/// the truth behind it - an <see cref="OverlayBar"/>'s own view state for a pane notch, a persisted
	/// view-model flag for the map-controls tier. That is what lets one tab sit on a rail above a surface
	/// it is not a child of.
	/// </para>
	/// </remarks>
	public sealed partial class OverlayBarTab : UserControl
	{
		public OverlayBarTab()
		{
			InitializeComponent();
			ApplyChrome();
			ApplySurface();
		}

		/// <summary>Whether the surface this tab toggles is currently shown. Two-way; the host owns the truth.</summary>
		public bool IsShown
		{
			get => (bool)GetValue(IsShownProperty);
			set => SetValue(IsShownProperty, value);
		}

		public static readonly DependencyProperty IsShownProperty =
			DependencyProperty.Register(nameof(IsShown), typeof(bool), typeof(OverlayBarTab), new PropertyMetadata(true));

		/// <summary>
		/// Which edge the tab hangs off: <see cref="BarEdge.Bottom"/> = the tab sits ABOVE its surface
		/// (the bar and the rail), <see cref="BarEdge.Top"/> = it hangs BELOW it (a pane notch).
		/// </summary>
		public BarEdge Edge
		{
			get => (BarEdge)GetValue(EdgeProperty);
			set => SetValue(EdgeProperty, value);
		}

		public static readonly DependencyProperty EdgeProperty =
			DependencyProperty.Register(nameof(Edge), typeof(BarEdge), typeof(OverlayBarTab),
				new PropertyMetadata(BarEdge.Bottom, OnChromeChanged));

		/// <summary>Whether the tab carries a word beside its chevron (see the two label modes in the XAML header).</summary>
		public bool ShowLabel
		{
			get => (bool)GetValue(ShowLabelProperty);
			set => SetValue(ShowLabelProperty, value);
		}

		public static readonly DependencyProperty ShowLabelProperty =
			DependencyProperty.Register(nameof(ShowLabel), typeof(bool), typeof(OverlayBarTab),
				new PropertyMetadata(true, OnChromeChanged));

		/// <summary>
		/// What this tab toggles, as a noun ("Tools", "Bar"). EMPTY means the tab is alone above its
		/// surface and reads the verb instead ("Hide" / "Show"); set it only when a rail carries more than
		/// one tab, where two verbs would name no difference between them.
		/// </summary>
		public string Target
		{
			get => (string)GetValue(TargetProperty);
			set => SetValue(TargetProperty, value);
		}

		public static readonly DependencyProperty TargetProperty =
			DependencyProperty.Register(nameof(Target), typeof(string), typeof(OverlayBarTab),
				new PropertyMetadata(string.Empty));

		/// <summary>
		/// Whether this tab wears the RAISED surface (the tier that sits on the bar) rather than the ground.
		/// Face only - the geometry is identical either way.
		/// </summary>
		public bool Raised
		{
			get => (bool)GetValue(RaisedProperty);
			set => SetValue(RaisedProperty, value);
		}

		public static readonly DependencyProperty RaisedProperty =
			DependencyProperty.Register(nameof(Raised), typeof(bool), typeof(OverlayBarTab),
				new PropertyMetadata(false, OnSurfaceChanged));

		private static void OnChromeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
			((OverlayBarTab)d).ApplyChrome();

		private static void OnSurfaceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
			((OverlayBarTab)d).ApplySurface();

		// Point the RESTING face at the right tier's surface. It is a visual state rather than a property
		// assignment because the value is a theme brush: resolving one in C# returns the OS theme's brush,
		// not the theme the app pinned on its root element (CLAUDE.md, and it shipped as a near-black key
		// on a light theme once already).
		private void ApplySurface() =>
			VisualStateManager.GoToState(TabButton, Raised ? "RaisedSurface" : "GroundSurface", false);

		// Shape the tab for its edge. ONE method, because the four values disagree with each other if they
		// are set apart: the open border side, the corner radius and the lap margin all flip together with
		// Edge, and the padding is the one that answers to ShowLabel instead.
		private void ApplyChrome()
		{
			bool top = Edge == BarEdge.Top;

			// The tab's inner edge is left BORDERLESS and lapped over the host surface's hairline, so the
			// two merge into one line that wraps around the tab instead of running across its foot.
			TabButton.BorderThickness = top ? new Thickness(1, 0, 1, 1) : new Thickness(1, 1, 1, 0);
			TabButton.Margin = top ? new Thickness(0, -2, 0, 0) : new Thickness(0, 0, 0, -2);

			// A tab with no word in it only has to hold a chevron, so it loses the label's side padding -
			// otherwise an unlabelled tab is a wide empty lozenge.
			TabButton.Padding = ShowLabel ? new Thickness(16, 3, 16, 3) : new Thickness(9, 2, 9, 2);

			// ⚠️ A LABELLED TAB IS A FIXED SIZE, and every tab in the app is labelled. Two reasons, both
			// in Controls/Styles.xaml beside the numbers: the verb-mode label flips "Hide"/"Show", which
			// would resize a content-sized tab on every click; and a content-sized tab takes its size from
			// its glyph's font metrics, which is nobody's decision - it is what made the pane notch's tab a
			// third the width of the bar's. The NaN branch is content-sizing for a wordless tab, of which
			// there are none today; it is not a second designed shape.
			TabButton.Width = ShowLabel ? SharedSize("OverlayBarTabWidth") : double.NaN;
			TabButton.Height = ShowLabel ? SharedSize("OverlayBarTabHeight") : double.NaN;
			TabButton.CornerRadius = top
				? new CornerRadius(0, 0, SharedSize("OverlayBarTabRadius"), SharedSize("OverlayBarTabRadius"))
				: new CornerRadius(SharedSize("OverlayBarTabRadius"), SharedSize("OverlayBarTabRadius"), 0, 0);

			// The chevron points TOWARD the surface for "hide", so it inverts with Edge.
			Bindings?.Update();
		}

		/// <summary>Reads one of the app-wide tab sizes from Controls/Styles.xaml. A mis-keyed lookup falls
		/// back to the value baked in here, so it degrades to a correct-looking tab rather than a zero-sized
		/// one. ⚠️ Safe as a C# lookup where a BRUSH would not be: a size has no theme.</summary>
		private static double SharedSize(string key) =>
			Application.Current.Resources.TryGetValue(key, out var v) && v is double d ? d
				: key == "OverlayBarTabRadius" ? 7 : key == "OverlayBarTabHeight" ? 28 : 96;

		// x:Bind function mapping a bool to Visibility (no value-converter lookup needed).
		public Visibility VisibleWhen(bool value) =>
			value ? Visibility.Visible : Visibility.Collapsed;

		private const string ChevronUp = "\uE70E";   // Segoe Fluent ChevronUp
		private const string ChevronDown = "\uE70D"; // Segoe Fluent ChevronDown

		/// <summary>The chevron points toward the surface it would collapse, so it inverts with both the
		/// state and the edge: bottom-edge hide = down, top-edge hide = up.</summary>
		public string Chevron(bool shown, BarEdge edge)
		{
			bool pointUp = edge == BarEdge.Top ? shown : !shown;
			return pointUp ? ChevronUp : ChevronDown;
		}

		/// <summary>The word on the tab: the noun when the rail has more than one tab, else the verb.</summary>
		public string Word(bool shown, string target) =>
			string.IsNullOrEmpty(target) ? (shown ? "Hide" : "Show") : target;

		/// <summary>The tooltip always carries the VERB - it is the only place a noun-mode tab states the action.</summary>
		public string Tooltip(bool shown, string target) =>
			string.IsNullOrEmpty(target)
				? (shown ? "Hide these controls" : "Show the controls")
				: (shown ? $"Hide the {target.ToLowerInvariant()}" : $"Show the {target.ToLowerInvariant()}");

		private void OnTabClick(object sender, RoutedEventArgs e) => IsShown = !IsShown;
	}
}
