using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.Models;
using Anvil.ViewModels;

namespace Anvil.Controls.Composites
{
	/// <summary>
	/// The ForeCast window body (see the XAML header) — the SPC outlook section (card over day / product /
	/// opacity) and the legend section. Bound to the coordinator <see cref="MapViewModel"/>; every control
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
				Anvil.Controls.Primitives.PanelSection.PersistExpansion(
					System.Linq.Enumerable.OfType<Anvil.Controls.Primitives.PanelSection>(Root.Children), "fore",
					ViewModel.IsSectionExpanded, ViewModel.SetSectionExpanded);
			};
		}

		// x:Bind helper: collapse a card line that has nothing to say, so the card closes up rather than
		// leaving a gap where the context or footer would be. Same helper as the other two bodies'.
		public Visibility HasText(string? value) =>
			string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;

		// x:Bind helper: the legend shows only while there is one AND the outlook section it keys is open.
		public Visibility LegendVisibility(bool hasLegend, bool sectionExpanded) =>
			hasLegend && sectionExpanded ? Visibility.Visible : Visibility.Collapsed;

		// x:Bind helper: a hatch group absent from today's outlook is dimmed as well as labelled. STATIC —
		// called from a DataTemplate. An Opacity, not a brush, so no theme brush is resolved in C#.
		public static double HatchRowOpacity(bool inOutlook) => inOutlook ? 1.0 : 0.45;

		// x:Bind helpers for the legend swatch's Conditional Intensity hatching. Deliberately split the
		// SAME way outlook.js's makeHatchImage takes its `back` / `fwd` flags: each asks "does this
		// pattern include this diagonal?", so the cross (CIG3) needs no case of its own and the swatch
		// cannot drift out of step with the map's tiles. STATIC because they are called from a
		// DataTemplate, whose x:Bind root is the SpcRiskLevel item, not this control.
		// ⚠️ The direction is the data here — every CIG level is black, so the pattern is the only thing
		// distinguishing group 1 from group 2. See SpcHatchPattern.
		public static Visibility HatchBackward(SpcHatchPattern pattern) =>
			pattern is SpcHatchPattern.BackwardDiagonal or SpcHatchPattern.DiagonalCross
				? Visibility.Visible : Visibility.Collapsed;

		public static Visibility HatchForward(SpcHatchPattern pattern) =>
			pattern is SpcHatchPattern.ForwardDiagonal or SpcHatchPattern.DiagonalCross
				? Visibility.Visible : Visibility.Collapsed;

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
