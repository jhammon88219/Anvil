using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.RadarSettingsConsoles
{
	/// <summary>
	/// Settings → Radar's Scrubber section (see the XAML header): which loop scrubber the console draws, previewed LIVE by the
	/// console's own scrubber control. Moved from the retired Radar Console page (2026-11-16).
	/// </summary>
	public sealed partial class RadarSettingsConsoleScrubber : UserControl
	{
		// What the preview shows while no loop is loaded: a loop mid-load — a lit run, the frame on screen inside it, a few
		// frames part-way, the rest empty.
		private const double SampleIndex = 5;
		private static readonly ObservableCollection<RadarFrameSegment> Sample = BuildSample();

		private RadarViewModel? _radar;

		public RadarSettingsConsoleScrubber()
		{
			InitializeComponent();
			Loaded += (_, _) => Attach();
			Unloaded += (_, _) => Detach(); // the VM outlives the Settings window — never leak into it
		}

		public MapViewModel ViewModel
		{
			get => (MapViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(RadarSettingsConsoleScrubber),
				new PropertyMetadata(null, (d, _) => ((RadarSettingsConsoleScrubber)d).Attach()));

		public Visibility Show(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

		private async void OnRestoreDefaults(object sender, RoutedEventArgs e)
		{
			if (ViewModel is { } vm && await Anvil.Dialogs.ConfirmResetDialog.ConfirmAsync(this, "Restore Scrubber default?",
				"The radar console's scrubber goes back to the DEFAULT style."))
			{
				vm.ResetScrubber();
			}
		}

		// ---- The preview's source: the loaded loop, else the sample ----

		private void Attach()
		{
			Detach();
			if (!IsLoaded || ViewModel?.Radar is not { } radar) return;
			_radar = radar;
			_radar.Segments.CollectionChanged += OnSegmentsChanged;
			_radar.PropertyChanged += OnRadarPropertyChanged;
			Feed();
		}

		private void Detach()
		{
			if (_radar is not null)
			{
				_radar.Segments.CollectionChanged -= OnSegmentsChanged;
				_radar.PropertyChanged -= OnRadarPropertyChanged;
			}
			_radar = null;
		}

		private void OnSegmentsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Feed();

		private void OnRadarPropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName == nameof(RadarViewModel.CurrentFrameIndex)) Feed();
		}

		private void Feed()
		{
			if (_radar is null) return;
			var live = _radar.Segments.Count > 0;
			var segments = live ? _radar.Segments : Sample;
			var index = live ? _radar.CurrentFrameIndex : SampleIndex;
			// A DP set to the SAME collection is a no-op, so flipping live ⇄ sample is the only re-attach.
			ClassicPreview.Segments = segments;
			ClassicPreview.CurrentIndex = index;
			DotMatrixPreview.Segments = segments;
			DotMatrixPreview.CurrentIndex = index;
			HatchPreview.Segments = segments;
			HatchPreview.CurrentIndex = index;
			ScrubberPreviewRow.Hint = Anvil.Services.SettingsText.ChoiceOf("scrubber.preview", live ? 0 : 1);
		}

		private static ObservableCollection<RadarFrameSegment> BuildSample()
		{
			var sample = new ObservableCollection<RadarFrameSegment>();
			double[] partial = { 0.85, 0.6, 0.35, 0.15 };
			for (var i = 0; i < 20; i++)
			{
				var ready = i < 9;
				var fill = ready ? 1 : i - 9 < partial.Length ? partial[i - 9] : 0;
				sample.Add(new RadarFrameSegment { IsDecoded = ready, IsReady = ready, Fill = fill });
			}
			return sample;
		}
	}
}
