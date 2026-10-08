using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Anvil.ViewModels;

namespace Anvil.Controls.Composites
{
	/// <summary>
	/// The bar's activity slot (see the XAML header). Every state is <see cref="BarActivityViewModel"/>'s; this
	/// draws it and carries the one door — hover + click on a loading loop = "Hold the map again".
	/// </summary>
	public sealed partial class BarActivityReadout : UserControl
	{
		// Tone literals (see the XAML header's ⚠️): radar = the gate's built-bar blue; done / failed = the old
		// site-check toast's green / red; housekeeping = a neutral that reads on both themes.
		private static readonly SolidColorBrush RadarBrush = new(ColorHelper.FromArgb(0xFF, 0x5B, 0x8D, 0xEF));
		private static readonly SolidColorBrush HousekeepingBrush = new(ColorHelper.FromArgb(0xFF, 0x8A, 0x8F, 0x98));
		private static readonly SolidColorBrush DoneBrush = new(ColorHelper.FromArgb(0xFF, 0x3F, 0xB9, 0x50));
		private static readonly SolidColorBrush FailedBrush = new(ColorHelper.FromArgb(0xFF, 0xF8, 0x51, 0x49));

		private bool _hovering;

		public BarActivityReadout()
		{
			InitializeComponent();
			Unloaded += (_, _) => Detach(ViewModel);
		}

		public BarActivityViewModel ViewModel
		{
			get => (BarActivityViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(BarActivityViewModel), typeof(BarActivityReadout),
				new PropertyMetadata(null, (d, e) =>
				{
					var readout = (BarActivityReadout)d;
					readout.Detach(e.OldValue as BarActivityViewModel);
					readout.Attach(e.NewValue as BarActivityViewModel);
				}));

		// Active = full strength; idle = dimmed, NEVER gone (the idle line and the empty plate stay readable).
		public double PlateOpacity(bool active) => active ? 1 : IdleOpacity;

		private const double IdleOpacity = 0.45;

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
			if (vm is not null) vm.PropertyChanged += OnViewModelPropertyChanged;
			ApplyTone();
		}

		private void Detach(BarActivityViewModel? vm)
		{
			if (vm is not null) vm.PropertyChanged -= OnViewModelPropertyChanged;
		}

		// The door can close under the pointer (the load finishes while you hover) — re-apply the hover look.
		private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName is nameof(BarActivityViewModel.CanReopen) or nameof(BarActivityViewModel.IsShown))
			{
				ApplyHover();
			}
			if (e.PropertyName is nameof(BarActivityViewModel.Tone) or nameof(BarActivityViewModel.IsShown))
			{
				ApplyTone();
			}
		}

		// THE TONE BORDER (see the XAML): a loud tone (radar work / done / failed) while the slot is active colours the ring;
		// otherwise it fades out keeping its last brush, so a flash fades in its own colour.
		private void ApplyTone()
		{
			var vm = ViewModel;
			var loud = vm is { IsShown: true } && vm.Tone is not (BarActivityTone.Idle or BarActivityTone.Housekeeping);
			if (loud) ToneBorder.BorderBrush = ToneBrush(vm!.Tone);
			ToneBorder.Opacity = loud ? 1 : 0;
		}

		private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
		{
			_hovering = true;
			ApplyHover();
		}

		private void OnPointerExited(object sender, PointerRoutedEventArgs e)
		{
			_hovering = false;
			ApplyHover();
		}

		private void ApplyHover()
		{
			var door = _hovering && ViewModel is { IsShown: true, CanReopen: true };
			HoverWash.Opacity = door ? 1 : 0;
			TitleText.Visibility = door ? Visibility.Collapsed : Visibility.Visible;
			ReopenText.Visibility = door ? Visibility.Visible : Visibility.Collapsed;
		}

		private void OnTapped(object sender, TappedRoutedEventArgs e) => ViewModel?.Reopen();
	}
}
