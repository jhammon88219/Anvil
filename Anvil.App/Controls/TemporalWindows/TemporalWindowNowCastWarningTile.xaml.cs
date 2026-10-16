using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Anvil.ViewModels;

namespace Anvil.Controls.TemporalWindows
{
	/// <summary>
	/// One warning tile under the NowCast header (see the XAML header): a band with the label and ‹ ›, the count,
	/// the state lines and the tag line. One control, three uses — Tornado / Severe / Flash flood.
	/// </summary>
	public sealed partial class TemporalWindowNowCastWarningTile : UserControl
	{
		public TemporalWindowNowCastWarningTile()
		{
			InitializeComponent();
		}

		/// <summary>The hazard's stepper: ‹ › walk its warnings, its state lines fill the tile. Bound from the host.</summary>
		public WarningStepper ViewModel
		{
			get => (WarningStepper)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}
		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(WarningStepper), typeof(TemporalWindowNowCastWarningTile), new PropertyMetadata(null));

		/// <summary>Every warning of this hazard in effect nationwide — never the ticked rows (user's call).</summary>
		public int Count
		{
			get => (int)GetValue(CountProperty);
			set => SetValue(CountProperty, value);
		}
		public static readonly DependencyProperty CountProperty =
			DependencyProperty.Register(nameof(Count), typeof(int), typeof(TemporalWindowNowCastWarningTile), new PropertyMetadata(0));

		/// <summary>The tag line (WarningThreatCounts, this hazard's split).</summary>
		public string Threats
		{
			get => (string)GetValue(ThreatsProperty);
			set => SetValue(ThreatsProperty, value);
		}
		public static readonly DependencyProperty ThreatsProperty =
			DependencyProperty.Register(nameof(Threats), typeof(string), typeof(TemporalWindowNowCastWarningTile), new PropertyMetadata(""));

		/// <summary>The name on the band: "Tornado" / "Severe" / "Flash flood".</summary>
		public string Label
		{
			get => (string)GetValue(LabelProperty);
			set => SetValue(LabelProperty, value);
		}
		public static readonly DependencyProperty LabelProperty =
			DependencyProperty.Register(nameof(Label), typeof(string), typeof(TemporalWindowNowCastWarningTile), new PropertyMetadata(""));

		/// <summary>The hazard in words, lower case, for the tooltips: "tornado" / "severe thunderstorm" / "flash flood".</summary>
		public string Hazard
		{
			get => (string)GetValue(HazardProperty);
			set => SetValue(HazardProperty, value);
		}
		public static readonly DependencyProperty HazardProperty =
			DependencyProperty.Register(nameof(Hazard), typeof(string), typeof(TemporalWindowNowCastWarningTile), new PropertyMetadata(""));

		/// <summary>The band's DEEP colour — a data literal, never themed.</summary>
		public Brush? BandFill
		{
			get => (Brush?)GetValue(BandFillProperty);
			set => SetValue(BandFillProperty, value);
		}
		public static readonly DependencyProperty BandFillProperty =
			DependencyProperty.Register(nameof(BandFill), typeof(Brush), typeof(TemporalWindowNowCastWarningTile), new PropertyMetadata(null));

		/// <summary>The BRIGHT colour of the label and the ‹ › — a data literal, never themed.</summary>
		public Brush? Ink
		{
			get => (Brush?)GetValue(InkProperty);
			set => SetValue(InkProperty, value);
		}
		public static readonly DependencyProperty InkProperty =
			DependencyProperty.Register(nameof(Ink), typeof(Brush), typeof(TemporalWindowNowCastWarningTile), new PropertyMetadata(null));

		// ── x:Bind functions ──

		/// <summary>A tile with nothing in effect is DIMMED, never hidden — the three keep their places.</summary>
		public double TileOpacity(int count) => count == 0 ? 0.4 : 1.0;

		/// <summary>An arrow's glyph wears Ink, which the button's disabled grey can't reach — so a disabled arrow dims itself.</summary>
		public double ArrowOpacity(bool enabled) => enabled ? 1.0 : 0.35;

		// The tag line ⇄ the "2 of 3 · place" line, by whether the arrows have you on a warning.
		public Visibility Shown(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
		public Visibility Hidden(bool on) => on ? Visibility.Collapsed : Visibility.Visible;

		// The rule above the state lines: only when there are warnings (a zero tile keeps its old shape).
		public Visibility NonZero(int count) => count > 0 ? Visibility.Visible : Visibility.Collapsed;

		public string TileTip(string hazard) =>
			$"{Capitalized(hazard)} warnings in effect nationwide — all of them, whatever the Warnings rows below show";
		public string BackTip(string hazard) => $"Back to the last {hazard} warning you visited";
		public string ForwardTip(string hazard) => $"Fly to the next {hazard} warning (worst first)";

		private static string Capitalized(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

		// ── The stepper ──

		private void OnBackClick(object sender, RoutedEventArgs e) => ViewModel?.Back();

		private void OnForwardClick(object sender, RoutedEventArgs e) => ViewModel?.Forward();

		private void OnCountTapped(object sender, TappedRoutedEventArgs e) => ViewModel?.Reset();
	}
}
