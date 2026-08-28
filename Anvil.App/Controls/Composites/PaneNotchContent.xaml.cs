using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.Composites
{
	/// <summary>
	/// The body of one pane's notch: that pane's product selector, its colour-ramp legend, and the tilt
	/// (see PaneNotchContent.xaml for the layout and for why tilt is drawn per pane while it is still one
	/// global value).
	///
	/// <para>Two view-model DPs rather than one: <see cref="Pane"/> carries what differs between panes
	/// (product, ramp, inspect read-out) and <see cref="Radar"/> what does not (the tilt, and whether a
	/// loop is loaded at all). When per-pane tilt lands, the Tilt combo moves onto Pane and this control's
	/// shape does not otherwise change.</para>
	/// </summary>
	public sealed partial class PaneNotchContent : UserControl
	{
		public PaneNotchContent()
		{
			InitializeComponent();
		}

		/// <summary>The pane this notch belongs to.</summary>
		public RadarPaneViewModel Pane
		{
			get => (RadarPaneViewModel)GetValue(PaneProperty);
			set => SetValue(PaneProperty, value);
		}

		public static readonly DependencyProperty PaneProperty =
			DependencyProperty.Register(nameof(Pane), typeof(RadarPaneViewModel), typeof(PaneNotchContent),
				new PropertyMetadata(null));

		/// <summary>The shared radar view model (tilt + whether the transport is live).</summary>
		public RadarViewModel Radar
		{
			get => (RadarViewModel)GetValue(RadarProperty);
			set => SetValue(RadarProperty, value);
		}

		public static readonly DependencyProperty RadarProperty =
			DependencyProperty.Register(nameof(Radar), typeof(RadarViewModel), typeof(PaneNotchContent),
				new PropertyMetadata(null));

		/// <summary>
		/// The tighter form: no scale numbers under the ramp and a shorter ramp. Set by the host for the
		/// layouts where a pane is only half the window wide (quad today).
		/// </summary>
		public bool IsCompact
		{
			get => (bool)GetValue(IsCompactProperty);
			set => SetValue(IsCompactProperty, value);
		}

		public static readonly DependencyProperty IsCompactProperty =
			DependencyProperty.Register(nameof(IsCompact), typeof(bool), typeof(PaneNotchContent),
				new PropertyMetadata(false, OnCompactChanged));

		// x:Bind functions do not re-evaluate on their own when a DP they read changes, so nudge the
		// generated bindings. (Both functions below take IsCompact as an argument for the same reason -
		// that is what makes them re-run at all.)
		private static void OnCompactChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
			((PaneNotchContent)d).Bindings?.Update();

		/// <summary>
		/// How wide the legend's ramp is drawn. FIXED per form, deliberately: the notch is content-sized,
		/// so a ramp left to stretch would let its own contents set the island's width and slide it around
		/// under the pane's centre line every time a product changed.
		/// </summary>
		public double RampWidth(bool compact) => compact ? 108 : 190;

		/// <summary>Compact hides the min/max row, which is also what takes the notch down to one line.</summary>
		public bool ShowScale(bool compact) => !compact;

		/// <summary>
		/// Dim the whole row while the notch is DORMANT - up on screen, but with no loop behind it, so
		/// every control in it is disabled. One opacity for the group rather than each control inventing
		/// its own disabled look, and the same cue the scrubber uses for the same state.
		/// </summary>
		public double DormantOpacity(bool hasLoop) => hasLoop ? 1.0 : 0.45;
	}
}
