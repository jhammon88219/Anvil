using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.TemporalPanes
{
	/// <summary>
	/// The PastCast card offering the two sources for a replay — your own timeframe, or a saved event from the Atlas
	/// (see the XAML header). Its edge lights while the picker's selection is dirty.
	/// </summary>
	public sealed partial class TemporalPanePastCastUserDecisionCard : UserControl
	{
		public TemporalPanePastCastUserDecisionCard()
		{
			InitializeComponent();
			// ⚠️ LIFETIME: Radar is an app singleton (its PropertyChanged fires on every playback tick) and the PastCast
			// window builds a new Content per open — listen only while loaded, or each closed window leaks.
			Loaded += (_, _) =>
			{
				if (ViewModel?.Radar is not RadarViewModel radar) return;
				radar.PropertyChanged -= OnRadarPropertyChanged; // never twice
				radar.PropertyChanged += OnRadarPropertyChanged;
				ApplySelectionState(); // catch a change made while unloaded
			};
			Unloaded += (_, _) => { if (ViewModel?.Radar is RadarViewModel radar) radar.PropertyChanged -= OnRadarPropertyChanged; };
		}

		/// <summary>The coordinator view model; bound from the host.</summary>
		public MapViewModel ViewModel
		{
			get => (MapViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(TemporalPanePastCastUserDecisionCard),
				new PropertyMetadata(null, (d, e) => ((TemporalPanePastCastUserDecisionCard)d).OnViewModelChanged(e)));

		// ===== The edge's accent state =====
		// ⚠️ THIS LISTENER EXISTS ONLY BECAUSE THE EDGE'S TWO BRUSHES ARE VISUAL STATES — see the XAML header for why
		// they had to stop being x:Bind functions. Every other value on this card is a plain x:Bind.
		private void OnViewModelChanged(DependencyPropertyChangedEventArgs e)
		{
			if (e.OldValue is MapViewModel old && old.Radar is RadarViewModel oldRadar)
			{
				oldRadar.PropertyChanged -= OnRadarPropertyChanged;
			}
			if (e.NewValue is MapViewModel now && now.Radar is RadarViewModel radar && IsLoaded)
			{
				radar.PropertyChanged += OnRadarPropertyChanged;
			}
			ApplySelectionState();
		}

		private void OnRadarPropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName == nameof(RadarViewModel.IsReplaySelectionDirty)) { ApplySelectionState(); }
		}

		private void ApplySelectionState() =>
			VisualStateManager.GoToState(this,
				ViewModel?.Radar?.IsReplaySelectionDirty == true ? "SelectionDirty" : "SelectionClean", false);

		// The type pill: only with a picked event that has a type (HasPlayingKind already implies a pick).
		public Visibility PillShown(bool playing, bool hasKind) => playing && hasKind ? Visibility.Visible : Visibility.Collapsed;

		// The Atlas door: the Anvil Atlas, on its Past events tab.
		private void OnOpenAtlasClick(object sender, RoutedEventArgs e) => ViewModel?.OpenAtlasOnEvents();
	}
}
