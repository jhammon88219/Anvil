using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.Controls.Primitives;
using Anvil.Models;
using Anvil.ViewModels;

namespace Anvil.Controls.Composites
{
	/// <summary>
	/// The ForeCast window body (see the XAML header) — the SPC outlook section (card over day / product +
	/// Discussion. / cycle, legend), plus NowCast's GHOST rows around it. Bound to the coordinator
	/// <see cref="MapViewModel"/>; every real control here drives <c>ViewModel.Outlook</c>.
	/// </summary>
	public sealed partial class ForeCastTab : UserControl
	{
		private bool _expansionBound;

		public ForeCastTab()
		{
			InitializeComponent();
			// Once: Loaded fires again every time the window re-shows this body.
			Loaded += (_, _) =>
			{
				if (_expansionBound || ViewModel is null) { return; }
				_expansionBound = true;
				// The SHARED Now + Fore order (ForeCast has none of its own): the outlook among NowCast's ghosts.
				PanelSection.ApplyLayerOrder(Sections, ViewModel.LayerOrderFor(TemporalMode.Now));
				// Ghosts have no body to open, so only the real sections persist their open/closed state.
				PanelSection.PersistExpansion(
					System.Linq.Enumerable.Where(System.Linq.Enumerable.OfType<PanelSection>(Sections.Children), s => !s.IsGhost),
					"fore", ViewModel.IsSectionExpanded, ViewModel.SetSectionExpanded);
			};
			// A drag in the NowCast window moves these too (and this window's drags move NowCast's).
			Loaded += (_, _) => { if (ViewModel is not null) { ViewModel.LayerOrderChanged += OnLayerOrderChanged; } };
			Unloaded += (_, _) => { if (ViewModel is not null) { ViewModel.LayerOrderChanged -= OnLayerOrderChanged; } };
		}

		// ⚠️ Saved as the NOW order: Now + Fore share one map, so they share one stack (MapViewModel's GHOST ROWS).
		private void OnSectionsReordered(object? sender, System.EventArgs e) =>
			ViewModel.SetLayerOrder(TemporalMode.Now, PanelSection.LayerOrderOf(Sections));

		private void OnLayerOrderChanged(object? sender, TemporalMode mode)
		{
			if (mode == TemporalMode.Now) { PanelSection.ApplyLayerOrder(Sections, ViewModel.LayerOrderFor(TemporalMode.Now)); }
		}

		// A ghost row shows only while its box is on and NowCast runs (MapViewModel.AreNowCastGhostsShown).
		public Visibility GhostVisibility(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;

		// "Discussion." on the Product row — SPC's forecast discussion in its own window.
		private void OnDiscussionClick(object sender, RoutedEventArgs e)
		{
			if (ViewModel is not null) { ViewModel.IsOutlookDiscussionOpen = true; }
		}

		// Header show/hide box: IsChecked is ONE-WAY, so the box has already flipped itself by the time Click
		// fires and the VM's answer overwrites it — the same contract as PastCast's outlook box.
		private void OnOutlookHeaderClick(object sender, RoutedEventArgs e) => ViewModel?.Outlook.ToggleShown();

		// x:Bind helper: collapse a card line that has nothing to say, so the card closes up rather than
		// leaving a gap where the context or footer would be. Same helper as the other two bodies'.
		public Visibility HasText(string? value) =>
			string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;

		/// <summary>The coordinator view model; bound from the host.</summary>
		public MapViewModel ViewModel
		{
			get => (MapViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(ForeCastTab), new PropertyMetadata(null));
	}
}
