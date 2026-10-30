using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.Controls.Primitives;
using Anvil.Models;
using Anvil.ViewModels;

namespace Anvil.Controls.TemporalPanes.TemporalPaneContents
{
	/// <summary>
	/// The ForeCast window body (see the XAML header) — the SPC outlook section (card over day / product +
	/// Discussion. / cycle, legend), plus NowCast's GHOST rows around it. Bound to the coordinator
	/// <see cref="MapViewModel"/>; every real control here drives <c>ViewModel.Outlook</c>.
	/// </summary>
	public sealed partial class TemporalPaneForeCastContent : UserControl
	{
		private bool _expansionBound;

		public TemporalPaneForeCastContent()
		{
			InitializeComponent();
			// Once: Loaded fires again every time the window re-shows this body.
			Loaded += (_, _) =>
			{
				if (_expansionBound || ViewModel is null) { return; }
				_expansionBound = true;
				// The SHARED Now + Fore order (ForeCast has none of its own): the outlook among NowCast's ghosts.
				DragMoveLayerExpanderHost.ApplyLayerOrder(ViewModel.LayerOrderFor(TemporalMode.Now));
				// The host skips ghosts (no body to open): only the real layers persist their open/closed state.
				DragMoveLayerExpanderHost.PersistExpansion("fore", ViewModel.IsSectionExpanded, ViewModel.SetSectionExpanded);
			};
			// A drag in the NowCast window moves these too (and this window's drags move NowCast's).
			Loaded += (_, _) => { if (ViewModel is not null) { ViewModel.LayerOrderChanged += OnLayerOrderChanged; } };
			Unloaded += (_, _) => { if (ViewModel is not null) { ViewModel.LayerOrderChanged -= OnLayerOrderChanged; } };
		}

		// ⚠️ Saved as the NOW order: Now + Fore share one map, so they share one stack (MapViewModel's GHOST ROWS).
		private void OnLayersReordered(object? sender, System.EventArgs e) =>
			ViewModel.SetLayerOrder(TemporalMode.Now, DragMoveLayerExpanderHost.LayerOrder);

		private void OnLayerOrderChanged(object? sender, TemporalMode mode)
		{
			if (mode == TemporalMode.Now) { DragMoveLayerExpanderHost.ApplyLayerOrder(ViewModel.LayerOrderFor(TemporalMode.Now)); }
		}

		// A ghost row shows only while its box is on and NowCast runs (MapViewModel.AreNowCastGhostsShown).
		public Visibility GhostVisibility(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;

		// The legend's fold, persisted like a section ("fore/outlook-legend"). Set on the LEGEND's Loaded — it sits in a
		// LAZY section body, so it doesn't exist until the section first opens.
		private const string LegendFoldKey = "fore/outlook-legend";

		private void OnOutlookLegendLoaded(object sender, RoutedEventArgs e)
		{
			if (sender is OutlookLegend legend && ViewModel is not null)
			{
				legend.IsExpanded = ViewModel.IsSectionExpanded(LegendFoldKey) ?? false;
			}
		}

		private void OnOutlookLegendExpandedChanged(object? sender, bool open) => ViewModel?.SetSectionExpanded(LegendFoldKey, open);

		// "Discussion." on the Cycle row — SPC's forecast discussion in its own window.
		private void OnDiscussionClick(object sender, RoutedEventArgs e)
		{
			if (ViewModel is not null) { ViewModel.IsOutlookDiscussionOpen = true; }
		}

		// Header show/hide box: IsChecked is ONE-WAY, so the box has already flipped itself by the time Click
		// fires and the VM's answer overwrites it — the same contract as PastCast's outlook box.
		private void OnOutlookHeaderClick(object sender, RoutedEventArgs e) => ViewModel?.Outlook.ToggleShown();

		// Short Range's box: the same one-way contract. Ticking it may turn NowCast off (MapViewModel's NowCast ⇄ MODEL).
		private void OnShortRangeHeaderClick(object sender, RoutedEventArgs e) => ViewModel?.ShortRange.ToggleShown();

		/// <summary>The coordinator view model; bound from the host.</summary>
		public MapViewModel ViewModel
		{
			get => (MapViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(TemporalPaneForeCastContent), new PropertyMetadata(null));
	}
}
