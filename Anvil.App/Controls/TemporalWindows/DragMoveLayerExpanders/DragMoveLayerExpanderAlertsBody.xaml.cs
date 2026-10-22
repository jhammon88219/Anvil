using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.TemporalWindows.DragMoveLayerExpanders
{
	/// <summary>
	/// The controls for one convective-alert overlay (see the XAML header): a summary card, a Tornado and a
	/// Severe thunderstorm toggle carrying that type's live count, and overlay opacity. Bound to a
	/// <see cref="PhenomOverlayViewModel"/> — the NowCast window shows one instance for the watch boxes and
	/// one for the storm-based warnings.
	/// </summary>
	public sealed partial class DragMoveLayerExpanderAlertsBody : UserControl
	{
		public DragMoveLayerExpanderAlertsBody()
		{
			InitializeComponent();
		}

		/// <summary>x:Bind formatter for the per-type counts (int → display string). Re-evaluates when the
		/// bound count property raises PropertyChanged.</summary>
		public string Fmt(int count) => count.ToString(System.Globalization.CultureInfo.InvariantCulture);

		/// <summary>Tooltip for the tornado row, e.g. "Show tornado warnings".</summary>
		public string TornadoTooltip(string noun) => $"Show tornado {noun}";

		/// <summary>Tooltip for the severe row, e.g. "Show severe thunderstorm watches".</summary>
		public string SevereTooltip(string noun) => $"Show severe thunderstorm {noun}";

		/// <summary>Tooltip for the flood row, e.g. "Show flash flood warnings" / "Show flood watches".</summary>
		public string FloodTooltip(string label, string noun) => $"Show {label?.ToLowerInvariant()} {noun}";

		// x:Bind helper: the flood row exists only on an overlay that has that type.
		public Visibility Shown(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

		// x:Bind helper: the flood swatch — a DATA colour (the map's literal), so a plain brush, never a theme one.
		public Microsoft.UI.Xaml.Media.Brush Swatch(string hex) =>
			new Microsoft.UI.Xaml.Media.SolidColorBrush(Anvil.Converters.ColorUtil.FromHex(hex));

		// The list's type square. ⚠️ DATA colours = the type rows' literals + warnings.js (via WarningsViewModel's
		// header fills); a NEW brush per row — this control lives in two OS windows, and a brush is a UI object.
		public static Microsoft.UI.Xaml.Media.Brush PhenomBrush(string phenom) =>
			new Microsoft.UI.Xaml.Media.SolidColorBrush(Anvil.Converters.ColorUtil.FromHex(phenom switch
			{
				"TO" => WarningsViewModel.TornadoFill,
				"SV" => WarningsViewModel.SevereFill,
				_ => WarningsViewModel.FlashFloodFill,
			}));

		/// <summary>An ended warning, or one whose type isn't drawn, dims (the reports list's 45%).</summary>
		public static double RowOpacity(bool dim) => dim ? 0.45 : 1.0;

		// A row click ACTS — fly there + flash the polygon (the tiles' arrows' focus); the list keeps no selection.
		private void OnAlertClick(object sender, ItemClickEventArgs e)
		{
			if (e.ClickedItem is WarningListRow row)
			{
				ViewModel?.FlyTo(row);
			}
		}

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
			DependencyProperty.Register(nameof(AlertNoun), typeof(string), typeof(DragMoveLayerExpanderAlertsBody), new PropertyMetadata("alerts"));

		/// <summary>The overlay's view model; bound from the host (TemporalWindowNowCastContent → ViewModel.Watches / .Warnings).</summary>
		public PhenomOverlayViewModel ViewModel
		{
			get => (PhenomOverlayViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(PhenomOverlayViewModel), typeof(DragMoveLayerExpanderAlertsBody), new PropertyMetadata(null));
	}
}
