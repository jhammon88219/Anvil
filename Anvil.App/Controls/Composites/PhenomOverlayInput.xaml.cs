using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.Composites
{
	/// <summary>
	/// The controls for one convective-alert overlay (see the XAML header): a summary card, a Tornado and a
	/// Severe thunderstorm toggle carrying that type's live count, and overlay opacity. Bound to a
	/// <see cref="PhenomOverlayViewModel"/> — the NowCast window shows one instance for the watch boxes and
	/// one for the storm-based warnings.
	/// </summary>
	public sealed partial class PhenomOverlayInput : UserControl
	{
		public PhenomOverlayInput()
		{
			InitializeComponent();
		}

		/// <summary>x:Bind formatter for the per-type counts (int → display string). Re-evaluates when the
		/// bound count property raises PropertyChanged.</summary>
		public string Fmt(int count) => count.ToString(System.Globalization.CultureInfo.InvariantCulture);

		// x:Bind helper: collapse a card line that has nothing to say, so the card closes up rather than
		// leaving a gap where the context or footer would be. Same helper as StormReportsInput's.
		public Visibility HasText(string? value) =>
			string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;

		/// <summary>Tooltip for the tornado row, e.g. "Show tornado warnings".</summary>
		public string TornadoTooltip(string noun) => $"Show tornado {noun}";

		/// <summary>Tooltip for the severe row, e.g. "Show severe thunderstorm watches".</summary>
		public string SevereTooltip(string noun) => $"Show severe thunderstorm {noun}";

		/// <summary>
		/// Plural noun for what this instance draws — "watches" or "warnings". The rows are labelled by
		/// PHENOMENON ("Tornado", "Severe thunderstorm") because the section heading already says which
		/// alert they are; this is what puts the missing half back into their tooltips.
		/// </summary>
		public string AlertNoun
		{
			get => (string)GetValue(AlertNounProperty);
			set => SetValue(AlertNounProperty, value);
		}

		public static readonly DependencyProperty AlertNounProperty =
			DependencyProperty.Register(nameof(AlertNoun), typeof(string), typeof(PhenomOverlayInput), new PropertyMetadata("alerts"));

		/// <summary>The overlay's view model; bound from the host (NowCastTab → ViewModel.Watches / .Warnings).</summary>
		public PhenomOverlayViewModel ViewModel
		{
			get => (PhenomOverlayViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(PhenomOverlayViewModel), typeof(PhenomOverlayInput), new PropertyMetadata(null));
	}
}
