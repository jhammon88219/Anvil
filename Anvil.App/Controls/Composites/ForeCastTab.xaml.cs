using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.Models;
using Anvil.ViewModels;

namespace Anvil.Controls.Composites
{
	/// <summary>
	/// The ForeCast window body (see the XAML header) — the SPC outlook section (card over day / product /
	/// opacity, legend) and the forecast discussion. Bound to the coordinator <see cref="MapViewModel"/>; every control
	/// here drives <c>ViewModel.Outlook</c>.
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
				// The layer run (Sections) + the non-layer sections directly under Root (the discussion).
				var sections = System.Linq.Enumerable.Concat(
					System.Linq.Enumerable.OfType<Anvil.Controls.Primitives.PanelSection>(Sections.Children),
					System.Linq.Enumerable.OfType<Anvil.Controls.Primitives.PanelSection>(Root.Children));
				Anvil.Controls.Primitives.PanelSection.PersistExpansion(sections, "fore",
					ViewModel.IsSectionExpanded, ViewModel.SetSectionExpanded);
			};
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
