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

		// Shape the tab for its edge and label: pick the EdgeStates / LabelStates declared on this control's root (the
		// open border side, the lap margin and the corners flip together with Edge; the padding and size answer to
		// ShowLabel). ONE method, so the two picks never drift apart.
		private void ApplyChrome()
		{
			VisualStateManager.GoToState(this, Edge == BarEdge.Top ? "TopEdge" : "BottomEdge", false);
			VisualStateManager.GoToState(this, ShowLabel ? "Labelled" : "Wordless", false);

			// Re-evaluate the glyph (it depends on Edge), the word and the tooltip.
			Bindings?.Update();
		}

		// x:Bind function mapping a bool to Visibility (no value-converter lookup needed).
		public Visibility VisibleWhen(bool value) =>
			value ? Visibility.Visible : Visibility.Collapsed;

		private const string ChevronUp = "\uE70E";   // Segoe Fluent ChevronUp
		private const string ChevronDown = "\uE70D"; // Segoe Fluent ChevronDown

		/// <summary>
		/// The arrow points THE WAY THE PANEL WILL MOVE. On a bottom-edge tab that is down to hide and up to
		/// show; on a top-edge pane notch it is the reverse - up to hide, down to show.
		/// </summary>
		/// <remarks>
		/// ⚠️⚠️ IT TAKES <see cref="Edge"/> AND MUST KEEP TAKING IT. It was briefly made edge-blind ("up = show,
		/// down = hide, everywhere") after "up means show" was said about the BOTTOM tabs and read as a
		/// universal rule. On a notch hanging from the top of a pane that put a DOWN arrow on an open notch
		/// whose click collapses it UPWARD - every notch pointing the opposite way at the opposite time. The
		/// rule was always motion, and motion depends on which edge the panel is attached to.
		/// </remarks>
		public string Chevron(bool shown, BarEdge edge)
		{
			// Bottom edge: shown -> hide moves it DOWN.  Top edge: shown -> hide moves it UP.
			bool pointUp = edge == BarEdge.Top ? shown : !shown;
			return pointUp ? ChevronUp : ChevronDown;
		}

		/// <summary>The word on the tab: <see cref="Label"/> when set, else the noun when the rail has more than one tab,
		/// else the verb.</summary>
		public string Word(bool shown, string target, string label) =>
			!string.IsNullOrEmpty(label) ? label
			: string.IsNullOrEmpty(target) ? (shown ? "Hide" : "Show") : target;

		/// <summary>The tooltip always carries the VERB - it is the only place a noun-mode tab states the action.
		/// <see cref="Hint"/> replaces it when set.</summary>
		public string Tooltip(bool shown, string target, string hint) =>
			!string.IsNullOrEmpty(hint) ? hint
			: string.IsNullOrEmpty(target)
				? (shown ? "Hide these controls" : "Show the controls")
				: (shown ? $"Hide the {target.ToLowerInvariant()}" : $"Show the {target.ToLowerInvariant()}");

		// ── MORE THAN TWO STATES (the temporal pane's activity expander, 2026-10-17) ──
		// A host with its own state machine sets ManualToggle: a click raises Clicked and leaves IsShown alone; the host
		// moves its state and re-points IsShown (the chevron) and Label (the word) itself. Default off = every existing tab.

		/// <summary>Replaces the word ("More" / "Less"). Empty = the Target / verb rule.</summary>
		public string Label
		{
			get => (string)GetValue(LabelProperty);
			set => SetValue(LabelProperty, value);
		}

		public static readonly DependencyProperty LabelProperty =
			DependencyProperty.Register(nameof(Label), typeof(string), typeof(OverlayBarTab), new PropertyMetadata(string.Empty));

		/// <summary>Replaces the tooltip. Empty = the verb rule.</summary>
		public string Hint
		{
			get => (string)GetValue(HintProperty);
			set => SetValue(HintProperty, value);
		}

		public static readonly DependencyProperty HintProperty =
			DependencyProperty.Register(nameof(Hint), typeof(string), typeof(OverlayBarTab), new PropertyMetadata(string.Empty));

		/// <summary>True = a click raises <see cref="Clicked"/> only; the host owns <see cref="IsShown"/>.</summary>
		public bool ManualToggle
		{
			get => (bool)GetValue(ManualToggleProperty);
			set => SetValue(ManualToggleProperty, value);
		}

		public static readonly DependencyProperty ManualToggleProperty =
			DependencyProperty.Register(nameof(ManualToggle), typeof(bool), typeof(OverlayBarTab), new PropertyMetadata(false));

		/// <summary>Every click (both modes).</summary>
		public event RoutedEventHandler? Clicked;

		private void OnTabClick(object sender, RoutedEventArgs e)
		{
			if (!ManualToggle) IsShown = !IsShown;
			Clicked?.Invoke(this, e);
		}
	}
}
