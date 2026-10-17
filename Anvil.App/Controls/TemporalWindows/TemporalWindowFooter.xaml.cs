using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Anvil.ViewModels;

namespace Anvil.Controls.TemporalWindows
{
	/// <summary>
	/// A temporal window's activity band (see the XAML header). Every state is <see cref="BarActivityViewModel"/>'s;
	/// this only draws it. Null <see cref="Activity"/> (a mode with no activity sources yet) = an empty band.
	/// </summary>
	public sealed partial class TemporalWindowFooter : UserControl
	{
		// Tone literals (see the XAML header's ⚠️): radar = the gate's built-bar blue; done / failed = the old
		// site-check toast's green / red; housekeeping = a neutral that reads on both themes.
		private static readonly SolidColorBrush RadarBrush = new(ColorHelper.FromArgb(0xFF, 0x5B, 0x8D, 0xEF));
		private static readonly SolidColorBrush HousekeepingBrush = new(ColorHelper.FromArgb(0xFF, 0x8A, 0x8F, 0x98));
		private static readonly SolidColorBrush DoneBrush = new(ColorHelper.FromArgb(0xFF, 0x3F, 0xB9, 0x50));
		private static readonly SolidColorBrush FailedBrush = new(ColorHelper.FromArgb(0xFF, 0xF8, 0x51, 0x49));

		private const double IdleOpacity = 0.45;

		public TemporalWindowFooter()
		{
			InitializeComponent();
			Unloaded += (_, _) => Detach(Activity);
		}

		/// <summary>This mode's activity, or null while the mode has no sources (Past / Fore today).</summary>
		public BarActivityViewModel? Activity
		{
			get => (BarActivityViewModel?)GetValue(ActivityProperty);
			set => SetValue(ActivityProperty, value);
		}

		public static readonly DependencyProperty ActivityProperty =
			DependencyProperty.Register(nameof(Activity), typeof(BarActivityViewModel), typeof(TemporalWindowFooter),
				new PropertyMetadata(null, (d, e) =>
				{
					var footer = (TemporalWindowFooter)d;
					footer.Detach(e.OldValue as BarActivityViewModel);
					footer.Attach(e.NewValue as BarActivityViewModel);
				}));

		public Visibility HasActivity(BarActivityViewModel? activity) => activity is null ? Visibility.Collapsed : Visibility.Visible;

		// Active = full strength; idle = dimmed, NEVER gone (the idle line stays readable).
		public double LinesOpacity(bool active) => active ? 1 : IdleOpacity;

		public Visibility HasText(string text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

		public GridLength Filled(double fraction) => new(System.Math.Clamp(fraction, 0, 1), GridUnitType.Star);

		public GridLength Rest(double fraction) => new(1 - System.Math.Clamp(fraction, 0, 1), GridUnitType.Star);

		public Brush ToneBrush(BarActivityTone tone) => tone switch
		{
			BarActivityTone.Housekeeping or BarActivityTone.Idle => HousekeepingBrush,
			BarActivityTone.Done => DoneBrush,
			BarActivityTone.Failed => FailedBrush,
			_ => RadarBrush,
		};

		private void Attach(BarActivityViewModel? vm)
		{
			if (vm is not null) vm.PropertyChanged += OnActivityPropertyChanged;
			ApplyTone();
		}

		private void Detach(BarActivityViewModel? vm)
		{
			if (vm is not null) vm.PropertyChanged -= OnActivityPropertyChanged;
		}

		private void OnActivityPropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName is nameof(BarActivityViewModel.Tone) or nameof(BarActivityViewModel.IsShown))
			{
				ApplyTone();
			}
		}

		// THE TONE EDGE (see the XAML): a loud tone (radar work / done / failed) while active colours the top line;
		// otherwise it fades out keeping its last brush, so a flash fades in its own colour.
		private void ApplyTone()
		{
			var vm = Activity;
			var loud = vm is { IsShown: true } && vm.Tone is not (BarActivityTone.Idle or BarActivityTone.Housekeeping);
			if (loud) ToneEdge.Background = ToneBrush(vm!.Tone);
			ToneEdge.Opacity = loud ? 1 : 0;
		}
	}
}
