using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Anvil.ViewModels;

namespace Anvil.Controls.TemporalWindows
{
	/// <summary>
	/// A temporal window's activity band in three states (see the XAML header). Every state is
	/// <see cref="TemporalActivityViewModel"/>'s (and its radar's / feeds'); this only draws it and forwards the tab.
	/// </summary>
	public sealed partial class TemporalWindowActivityExpander : UserControl
	{
		// Tone / state literals (see the XAML header's ⚠️): radar = the gate's built-bar blue; done / failed = the old
		// site-check toast's green / red; waiting / housekeeping = a neutral that reads on both themes.
		private static readonly SolidColorBrush RadarBrush = new(ColorHelper.FromArgb(0xFF, 0x5B, 0x8D, 0xEF));
		private static readonly SolidColorBrush NeutralBrush = new(ColorHelper.FromArgb(0xFF, 0x8A, 0x8F, 0x98));
		private static readonly SolidColorBrush DoneBrush = new(ColorHelper.FromArgb(0xFF, 0x3F, 0xB9, 0x50));
		private static readonly SolidColorBrush FailedBrush = new(ColorHelper.FromArgb(0xFF, 0xF8, 0x51, 0x49));
		private static readonly SolidColorBrush DownBrush = new(ColorHelper.FromArgb(0xFF, 0xD2, 0x99, 0x22)); // a live source lost

		private const double IdleOpacity = 0.45;

		private BarActivityViewModel? _radar;

		public TemporalWindowActivityExpander()
		{
			InitializeComponent();
			Unloaded += (_, _) => DetachRadar();
		}

		/// <summary>This window's activity (MapViewModel.ActivityFor(mode)).</summary>
		public TemporalActivityViewModel? Activity
		{
			get => (TemporalActivityViewModel?)GetValue(ActivityProperty);
			set => SetValue(ActivityProperty, value);
		}

		public static readonly DependencyProperty ActivityProperty =
			DependencyProperty.Register(nameof(Activity), typeof(TemporalActivityViewModel), typeof(TemporalWindowActivityExpander),
				new PropertyMetadata(null, (d, e) => ((TemporalWindowActivityExpander)d).AttachRadar()));

		private void OnTabClicked(object sender, RoutedEventArgs e) => Activity?.Advance();

		// ── x:Bind helpers ──

		public Visibility Shown(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

		public Visibility ShownWhen(bool a, bool b) => a && b ? Visibility.Visible : Visibility.Collapsed;

		public bool Not(bool value) => !value;

		public string TabHint(bool opensMore, bool past) => !opensMore ? "Show less" : past ? "Show what this event downloaded" : "Show every feed's times";

		// Active = full strength; idle = dimmed, NEVER gone (the idle line stays readable).
		public double LinesOpacity(bool active) => active ? 1 : IdleOpacity;

		// A busy bar keeps its slot (the height holds) and only shows while the radar is busy — the idle countdown fill
		// is gone with every other countdown (2026-10-19).
		public double BusyOpacity(bool busy, bool has) => busy && has ? 1 : 0;

		public Visibility HasText(string text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

		public GridLength Filled(double fraction) => new(System.Math.Clamp(fraction, 0, 1), GridUnitType.Star);

		public GridLength Rest(double fraction) => new(1 - System.Math.Clamp(fraction, 0, 1), GridUnitType.Star);

		public Brush ToneBrush(BarActivityTone tone) => tone switch
		{
			BarActivityTone.Housekeeping or BarActivityTone.Idle => NeutralBrush,
			BarActivityTone.Done => DoneBrush,
			BarActivityTone.Failed => FailedBrush,
			_ => RadarBrush,
		};

		// The source line's dot: live = done green · down / needs an account = amber · off / connecting = neutral.
		public Brush LinkBrush(Anvil.Services.LiveLinkState state) => state switch
		{
			Anvil.Services.LiveLinkState.Connected => DoneBrush,
			Anvil.Services.LiveLinkState.Down or Anvil.Services.LiveLinkState.NeedsAccount => DownBrush,
			_ => NeutralBrush,
		};

		// ── The tone edge follows the radar's tone ──

		private void AttachRadar()
		{
			DetachRadar();
			_radar = Activity?.Radar;
			if (_radar is not null) _radar.PropertyChanged += OnRadarPropertyChanged;
			ApplyTone();
		}

		private void DetachRadar()
		{
			if (_radar is not null) _radar.PropertyChanged -= OnRadarPropertyChanged;
			_radar = null;
		}

		private void OnRadarPropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName is nameof(BarActivityViewModel.Tone) or nameof(BarActivityViewModel.IsShown)) ApplyTone();
		}

		// THE TONE EDGE: a loud tone (radar work / done / failed) while active colours the top line; otherwise it fades
		// out keeping its last brush, so a flash fades in its own colour.
		private void ApplyTone()
		{
			var r = _radar;
			var loud = r is { IsShown: true } && r.Tone is not (BarActivityTone.Idle or BarActivityTone.Housekeeping);
			if (loud) ToneEdge.Background = ToneBrush(r!.Tone);
			ToneEdge.Opacity = loud ? 1 : 0;
		}
	}
}
