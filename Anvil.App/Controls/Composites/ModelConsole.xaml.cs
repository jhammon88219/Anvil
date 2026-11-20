using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Anvil.ViewModels;

namespace Anvil.Controls.Composites
{
	/// <summary>
	/// The bottom console's MODEL MODE (see the XAML header): clock = the valid time, the scrubber over the model run's
	/// frames, transport, and Model / Run / Product. Bound to <see cref="ModelPlaybackViewModel"/>; laid out exactly like
	/// <see cref="RadarControls"/>, over which MainWindow shows it while a model field draws.
	/// </summary>
	public sealed partial class ModelConsole : UserControl
	{
		public ModelConsole()
		{
			InitializeComponent();
		}

		/// <summary>The playback view model; bound from the host (MainWindow → ViewModel.ShortRange.Playback).</summary>
		public ModelPlaybackViewModel ViewModel
		{
			get => (ModelPlaybackViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(ModelPlaybackViewModel), typeof(ModelConsole), new PropertyMetadata(null));

		/// <summary>Radar's scrubber look (Settings → Radar Console), so the two consoles never differ.</summary>
		public bool UseDotMatrix
		{
			get => (bool)GetValue(UseDotMatrixProperty);
			set => SetValue(UseDotMatrixProperty, value);
		}

		public static readonly DependencyProperty UseDotMatrixProperty =
			DependencyProperty.Register(nameof(UseDotMatrix), typeof(bool), typeof(ModelConsole), new PropertyMetadata(false));

		/// <summary>Radar's hatch scrubber is picked (Settings → Radar Console) — the third look.</summary>
		public bool UseHatch
		{
			get => (bool)GetValue(UseHatchProperty);
			set => SetValue(UseHatchProperty, value);
		}

		public static readonly DependencyProperty UseHatchProperty =
			DependencyProperty.Register(nameof(UseHatch), typeof(bool), typeof(ModelConsole), new PropertyMetadata(false));

		/// <summary>Radar's hatch scrubber look (Settings → Radar Console, RadarViewModel.HatchLook), so the two consoles match.</summary>
		public HatchScrubberLookViewModel? HatchLook
		{
			get => (HatchScrubberLookViewModel?)GetValue(HatchLookProperty);
			set => SetValue(HatchLookProperty, value);
		}

		public static readonly DependencyProperty HatchLookProperty =
			DependencyProperty.Register(nameof(HatchLook), typeof(HatchScrubberLookViewModel), typeof(ModelConsole), new PropertyMetadata(null));

		public Visibility ClassicVisibility(bool dots, bool hatch) => dots || hatch ? Visibility.Collapsed : Visibility.Visible;
		public Visibility DotVisibility(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;

		private void OnPlayStopClick(object sender, RoutedEventArgs e)
		{
			if (ViewModel is null) return;
			if (ViewModel.IsPlaying) ViewModel.Stop();
			else ViewModel.TogglePlay();
		}

		private void OnPrevClick(object sender, RoutedEventArgs e) => ViewModel?.StepFrame(-1);
		private void OnNextClick(object sender, RoutedEventArgs e) => ViewModel?.StepFrame(+1);
		private void OnCheckNowClick(object sender, RoutedEventArgs e) => ViewModel?.CheckNow();

		// ---- Scrubber: a COPY of RadarControls' mapping (equal slots / DotMatrixScrubber.IndexAt) — change both. ----
		private bool _scrubbing;
		private double _dragOriginX; // the scrubber's left edge at PRESS (RadarControls' note on why)

		private void OnScrubberPointerPressed(object sender, PointerRoutedEventArgs e)
		{
			if (ViewModel is null || !ViewModel.IsTransportEnabled || ViewModel.Segments.Count == 0) return;
			_scrubbing = true;
			_dragOriginX = ScrubberHost.TransformToVisual(null).TransformPoint(default).X;
			ScrubberHost.CapturePointer(e.Pointer);
			if (ViewModel.IsPlaying) ViewModel.TogglePlay(); // pause in place
			SeekToPointer(e);
		}

		private void OnScrubberPointerMoved(object sender, PointerRoutedEventArgs e)
		{
			if (_scrubbing) SeekToPointer(e);
		}

		private void OnScrubberPointerReleased(object sender, PointerRoutedEventArgs e)
		{
			if (!_scrubbing) return;
			_scrubbing = false;
			ScrubberHost.ReleasePointerCapture(e.Pointer);
		}

		private void SeekToPointer(PointerRoutedEventArgs e)
		{
			if (ViewModel is null) return;
			var count = ViewModel.Segments.Count;
			var width = ScrubberHost.ActualWidth;
			if (count <= 0 || width <= 0) return;
			var x = e.GetCurrentPoint(null).Position.X - _dragOriginX;
			var idx = UseDotMatrix && DotScrubber.IndexAt(x) is var dot and >= 0
				? dot
				: Math.Clamp((int)Math.Floor(x / (width / count)), 0, count - 1);
			ViewModel.CurrentFrameIndex = idx;
		}

		public string PlayStopGlyph(bool isPlaying) => isPlaying ? "" : "";

		// RadarControls' fixed widths, so every slot sits where radar's does (see its XAML header).
		public double ClockBlockWidth => RadarControls.ClockBlockWidthPx;
		public double FrameCountWidth => RadarControls.FrameCountWidthPx;
		public double ScanLineWidth => RadarControls.ScanLineWidthPx;

		/// <summary>The strip's ends from the window's edges — RadarControls.SideInsets, set by the same host.</summary>
		public Thickness SideInsets
		{
			get => (Thickness)GetValue(SideInsetsProperty);
			set => SetValue(SideInsetsProperty, value);
		}

		public static readonly DependencyProperty SideInsetsProperty =
			DependencyProperty.Register(nameof(SideInsets), typeof(Thickness), typeof(ModelConsole), new PropertyMetadata(default(Thickness)));

		public Thickness RightBlockMargin(Thickness insets) =>
			new(RadarControls.BlockGap, 0, insets.Right + RadarControls.BlockGap, 0);
	}
}
