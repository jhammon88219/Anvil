using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.Composites
{
	/// <summary>
	/// The Settings window's Radar Console tab (see the XAML header): which loop scrubber the bar's console draws,
	/// each option previewed LIVE by the console's own scrubber control. The choice lives on
	/// <see cref="RadarViewModel.ScrubberStyleIndex"/>.
	/// </summary>
	public sealed partial class RadarConsoleSettingsTab : UserControl
	{
		// What a preview shows while no loop is loaded: a loop mid-load — a lit run, the frame on screen inside it,
		// a few frames part-way, the rest empty.
		private const double SampleIndex = 5;
		private static readonly ObservableCollection<RadarFrameSegment> Sample = BuildSample();

		private RadarViewModel? _radar;

		public RadarConsoleSettingsTab()
		{
			InitializeComponent();
			Loaded += (_, _) => Attach();
			Unloaded += (_, _) => Detach(); // the VM outlives the Settings window — never leak into it
		}

		/// <summary>The coordinator view model; bound from the host.</summary>
		public MapViewModel ViewModel
		{
			get => (MapViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(RadarConsoleSettingsTab),
				new PropertyMetadata(null, (d, _) => ((RadarConsoleSettingsTab)d).Attach()));

		/// <summary>One-way IsChecked for an option card.</summary>
		public bool IsStyle(int selected, int mine) => selected == mine;

		private void OnClassicClick(object sender, RoutedEventArgs e) => Pick(0);

		private void OnDotMatrixClick(object sender, RoutedEventArgs e) => Pick(1);

		private void Pick(int index)
		{
			if (ViewModel?.Radar is { } radar) radar.ScrubberStyleIndex = index;
		}

		// ---- The previews' source: the loaded loop, else the sample ----

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
			PreviewNote.Text = live
				? "Previews show the loop on the map."
				: "Previews show a sample loop until one loads.";
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
