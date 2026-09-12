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

			// ⚠⚠ RE-APPLY THE FACE ONCE THE TEMPLATE EXISTS, AND THIS IS NOT BELT-AND-BRACES.
			// SurfaceStates lives inside TabButton's ControlTemplate, and a Button's template is applied
			// on its first MEASURE - not in its constructor. VisualStateManager.GoToState is a silent
			// no-op until then (it returns false and nothing is written), so every ApplySurface that runs
			// during construction is thrown away.
			// Every host sets Raised exactly there: OverlayBar hands a notch's tab its face from
			// ApplyChrome, and MainWindow.ApplyRailSeating runs in the window's ctor - both inside
			// InitializeComponent, both before layout. The result shipped as pane-notch tabs that never
			// matched their own plate, and rail tabs that were wrong until the first time the tools tier
			// was toggled (which re-ran it, by then after layout - which is exactly why it looked like a
			// STARTUP bug rather than a broken property).
			// ⚠️ OverlayBar does NOT need this: its groups sit on its own root Grid, which exists the
			// moment InitializeComponent returns. Templated control = wait for the template.
			Loaded += (_, _) => ApplySurface();
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
		/// Whether this tab wears the RAISED surface rather than the ground. Face only - the geometry is
		/// identical either way.
		/// </summary>
		/// <remarks>
		/// ⚠️ It follows the plate DIRECTLY BENEATH THE TAB, never the thing the tab toggles: a tab reads
		/// as a piece of the surface it stands on. On MainWindow's rail that means both tabs move together
		/// as the map-tools tier comes and goes (MainWindow.ApplyRailSeating).
		/// </remarks>
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
		// ⚠️ Does nothing until TabButton's template is applied - see the Loaded hook in the ctor.
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

			// Re-evaluate the glyph, the word and the tooltip for the new shape.
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

		/// <summary>
		/// UP MEANS SHOW, DOWN MEANS HIDE - everywhere, whichever edge the tab hangs off.
		/// </summary>
		/// <remarks>
		/// ⚠️ The arrow states the ACTION, not a direction of travel, and it used to take
		/// <see cref="Edge"/> for exactly that reason: it pointed TOWARD the surface it would collapse, so
		/// a top-edge pane notch showed an UP arrow while it was open. Consistent as a motion metaphor,
		/// and backwards as a label - up on one tab meant "show" and up on another meant "hide". The tab
		/// answers one question now: which way does this click move things.
		/// </remarks>
		public string Chevron(bool shown) => shown ? ChevronDown : ChevronUp;

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
