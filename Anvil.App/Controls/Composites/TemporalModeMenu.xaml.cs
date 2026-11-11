using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.Composites
{
	/// <summary>
	/// The body of the top bar's Temporal Mode menu: one line per mode, the MODE on the left and its PANE (eye) on the
	/// right — two click targets per row. See the XAML header for the shape and the rules.
	/// </summary>
	public sealed partial class TemporalModeMenu : UserControl
	{
		public TemporalModeMenu()
		{
			InitializeComponent();
		}

		/// <summary>The coordinator view model; bound from the host.</summary>
		public MapViewModel ViewModel
		{
			get => (MapViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(TemporalModeMenu), new PropertyMetadata(null));

		/// <summary>Raised after every click — the host closes the flyout (a menu's behaviour).</summary>
		public event EventHandler? Acted;

		// ── x:Bind functions ──
		// The side that is OFF dims while the other side runs (Past excludes Now + Fore).
		public double DimWhen(bool other) => other ? 0.45 : 1.0;
		public double DimWhenEither(bool a, bool b) => DimWhen(a || b);

		// The ✓ keeps its column when off (so the names line up) — it is just not drawn.
		public double TickOpacity(bool on) => on ? 1.0 : 0.0;

		// E890 View (open eye) = pane shown, ED1A Hide = pane hidden. ⚠️ ED1A is UNVERIFIED.
		public string EyeGlyph(bool open) => open ? "" : "";
		public string PaneTip(bool open) => open ? "Hide this pane" : "Show this pane";

		// The greyed line under PastCast: what a click on the dimmed side turns off.
		public string Hint(bool now, bool fore, bool past) =>
			past ? "NowCast or ForeCast turns PastCast off"
			: now || fore ? "Turns NowCast and ForeCast off"
			: "";

		public Visibility HintVisibility(bool now, bool fore, bool past) =>
			now || fore || past ? Visibility.Visible : Visibility.Collapsed;

		// A dimmed mode target reads as DISABLED at rest, so it lifts to full strength under the pointer or keyboard
		// focus and settles back to the rule's dim after. (Opacity is x:Bind OneWay; this local value lasts until the
		// next mode change re-binds it, which yields the same rule anyway.)
		private void OnModeLift(object sender, RoutedEventArgs e) => ((UIElement)sender).Opacity = 1.0;

		private void OnModeSettle(object sender, RoutedEventArgs e)
		{
			if (ViewModel is not { } vm) return;
			((UIElement)sender).Opacity = ReferenceEquals(sender, TemporalModeMenuPastCastButton)
				? DimWhenEither(vm.IsNowCast, vm.IsForeCast)
				: DimWhen(vm.IsPastCast);
		}

		// ── Clicks: the MODE (MapViewModel.ToggleTemporalMode — the ONLY place a mode turns on/off) or its PANE
		// (MapViewModel.TogglePane — the same value View's pane items tick). ──
		private void OnNowCastClick(object sender, RoutedEventArgs e) => Act(vm => vm.ToggleTemporalMode(TemporalMode.Now));
		private void OnForeCastClick(object sender, RoutedEventArgs e) => Act(vm => vm.ToggleTemporalMode(TemporalMode.Fore));
		private void OnPastCastClick(object sender, RoutedEventArgs e) => Act(vm => vm.ToggleTemporalMode(TemporalMode.Past));
		private void OnNowPaneClick(object sender, RoutedEventArgs e) => Act(vm => vm.TogglePane(TemporalMode.Now));
		private void OnForePaneClick(object sender, RoutedEventArgs e) => Act(vm => vm.TogglePane(TemporalMode.Fore));
		private void OnPastPaneClick(object sender, RoutedEventArgs e) => Act(vm => vm.TogglePane(TemporalMode.Past));

		private void Act(Action<MapViewModel> action)
		{
			if (ViewModel is { } vm) action(vm);
			Acted?.Invoke(this, EventArgs.Empty);
		}
	}
}
