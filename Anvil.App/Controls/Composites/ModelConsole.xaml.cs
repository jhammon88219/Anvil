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

		// The clock splits "h:mm tt" at its last space, or "17:04Z" before its Z: large digits, small AM/PM / Z
		// (RadarControls' rule).
		public string ClockDigits(string time) => RadarControls.SplitClock(time).Digits;

		public string ClockSuffix(string time) => RadarControls.SplitClock(time).Suffix;

		public string AmDesignator => System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.AMDesignator;
		public string PmDesignator => System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.PMDesignator;

		/// <summary>The right end of the LONGEST Model / Run / Product line's text in <paramref name="root"/>'s coordinates —
		/// where the tools tier's centre section ends while this console shows (RadarControls.ScanTextRight, copied).
		/// NaN before layout.</summary>
		public double ScanTextRight(UIElement root)
		{
			double edge = double.NaN;
			foreach (var child in ScanBlock.Children)
			{
				if (child is FrameworkElement text && text.Visibility == Visibility.Visible && text.ActualWidth > 0)
				{
					var right = text.TransformToVisual(root).TransformPoint(new Point(text.ActualWidth, 0)).X;
					edge = double.IsNaN(edge) ? right : Math.Max(edge, right);
				}
			}
			return edge;
		}

		// Only the time sizes the clock column; the line under it trims.
		private void OnClockTimeSizeChanged(object sender, SizeChangedEventArgs e) => WhenText.MaxWidth = e.NewSize.Width;

		// The PLAY BUTTON is the centre (RadarControls.CentreOnPlay, copied): pad the short side so the control's centre
		// is play's — so this play lands exactly where radar's does.
		private void OnAssemblySizeChanged(object sender, SizeChangedEventArgs e) => CentreOnPlay();

		private void CentreOnPlay()
		{
			if (Assembly.ActualWidth <= 0 || PlayStopButton.ActualWidth <= 0) return;
			var play = PlayStopButton.TransformToVisual(Assembly).TransformPoint(default).X + PlayStopButton.ActualWidth / 2;
			var skew = Assembly.ActualWidth - 2 * play;
			var margin = new Thickness(Math.Max(0, skew), 0, Math.Max(0, -skew), 0);
			if (Math.Abs(margin.Left - Assembly.Margin.Left) > 0.5 || Math.Abs(margin.Right - Assembly.Margin.Right) > 0.5)
			{
				Assembly.Margin = margin;
			}
		}
	}
}
