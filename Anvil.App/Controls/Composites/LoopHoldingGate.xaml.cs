using System.ComponentModel;
using System.Numerics;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Anvil.ViewModels;

namespace Anvil.Controls.Composites
{
	/// <summary>
	/// The loop holding gate (see the XAML header): dims the map band and swallows its input while a PastCast
	/// loop loads. Every state is <see cref="LoopHoldingGateViewModel"/>'s; this only fades and routes clicks.
	/// </summary>
	public sealed partial class LoopHoldingGate : UserControl
	{

		private int _version; // bumped per show/hide, so a stale delayed fade-in can't land

		public LoopHoldingGate()
		{
			InitializeComponent();
			UseMapButton.PointerEntered += (_, _) => UseMapButton.Translation = new Vector3(0, -2, 0);
			UseMapButton.PointerExited += (_, _) => UseMapButton.Translation = Vector3.Zero;
			UseMapButton.AddHandler(PointerPressedEvent,
				new PointerEventHandler((_, _) => UseMapButton.Translation = Vector3.Zero), true);
			Unloaded += (_, _) => Detach(ViewModel);
		}

		public LoopHoldingGateViewModel ViewModel
		{
			get => (LoopHoldingGateViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(LoopHoldingGateViewModel), typeof(LoopHoldingGate),
				new PropertyMetadata(null, (d, e) =>
				{
					var gate = (LoopHoldingGate)d;
					gate.Detach(e.OldValue as LoopHoldingGateViewModel);
					gate.Attach(e.NewValue as LoopHoldingGateViewModel);
				}));

		/// <summary>Height of the opaque chrome the gate runs UNDER at its bottom edge (MainWindow sets it), so the
		/// content centres on the map you can actually see.</summary>
		public double ContentBottomInset
		{
			get => (double)GetValue(ContentBottomInsetProperty);
			set => SetValue(ContentBottomInsetProperty, value);
		}

		public static readonly DependencyProperty ContentBottomInsetProperty =
			DependencyProperty.Register(nameof(ContentBottomInset), typeof(double), typeof(LoopHoldingGate),
				new PropertyMetadata(0.0));

		public Thickness BottomInset(double inset) => new(0, 0, 0, System.Math.Max(0, inset));

		public string EscapeWarning => LoopHoldingGateViewModel.EscapeWarning;

		// The event picture: the type pill's ink (a DATA colour — PastEventsAtlasTab.KindInkBrush), and which drawing.
		public Microsoft.UI.Xaml.Media.Brush KindInk(Anvil.Models.SavedEventKind kind) => PastEventsAtlasTab.KindInkBrush(kind);

		public Visibility KindShown(Anvil.Models.SavedEventKind kind, string which) =>
			kind.ToString() == which ? Visibility.Visible : Visibility.Collapsed;

		public Visibility SpacerShown(bool actions, bool confirm, bool ready) =>
			actions || confirm || ready ? Visibility.Visible : Visibility.Collapsed;

		private void Attach(LoopHoldingGateViewModel? vm)
		{
			if (vm is null) return;
			vm.PropertyChanged += OnViewModelPropertyChanged;
			Apply(vm.IsShown);
		}

		private void Detach(LoopHoldingGateViewModel? vm)
		{
			if (vm is null) return;
			vm.PropertyChanged -= OnViewModelPropertyChanged;
		}

		private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName == nameof(LoopHoldingGateViewModel.IsShown) && ViewModel is { } vm)
			{
				Apply(vm.IsShown);
			}
		}

		// Input is gated at once; the dim fades in only after FadeInDelayMs (and fades out straight away).
		private void Apply(bool shown)
		{
			var version = ++_version;
			Root.IsHitTestVisible = shown;
			if (!shown)
			{
				Root.Opacity = 0;
				UseMapButton.Translation = Vector3.Zero;
				return;
			}
			_ = FadeInAsync(version);
		}

		private async Task FadeInAsync(int version)
		{
			await Task.Delay(LoopHoldingGateViewModel.FadeInDelayMs); // the map's blur waits the same (RadarViewModel)
			if (version == _version && ViewModel?.IsShown == true)
			{
				Root.Opacity = 1; // OpacityTransition eases it in
			}
		}

		private async void OnCancelClick(object sender, RoutedEventArgs e)
		{
			if (ViewModel is { } vm) await vm.CancelLoadAsync();
		}

		private void OnUseMapClick(object sender, RoutedEventArgs e) => ViewModel?.RequestEscape();

		private void OnKeepWaitingClick(object sender, RoutedEventArgs e) => ViewModel?.KeepWaiting();

		private void OnConfirmUseMapClick(object sender, RoutedEventArgs e) => ViewModel?.UseMap();

		private void OnGotItClick(object sender, RoutedEventArgs e) => ViewModel?.AcknowledgeCancelled();

		private void OnViewReadyClick(object sender, RoutedEventArgs e) => ViewModel?.ViewReady();
	}
}
