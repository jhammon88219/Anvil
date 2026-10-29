using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.TemporalPanes.DragMoveLayerExpanders
{
	/// <summary>
	/// The storm-cell controls (see the XAML header): a card and one toggle per mark with the displayed scan's
	/// count. Bound to <see cref="StormCellsViewModel"/>; hosted by both the Now and Past windows.
	/// </summary>
	public sealed partial class DragMoveLayerExpanderStormCellsBody : UserControl
	{
		public DragMoveLayerExpanderStormCellsBody()
		{
			InitializeComponent();
		}

		/// <summary>x:Bind formatter for the counts.</summary>
		public string Fmt(int count) => count.ToString(System.Globalization.CultureInfo.InvariantCulture);

		/// <summary>The view model; bound from the host (TemporalPaneNowCastContent / TemporalPanePastCastContent → ViewModel.StormCells).</summary>
		public StormCellsViewModel ViewModel
		{
			get => (StormCellsViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(StormCellsViewModel), typeof(DragMoveLayerExpanderStormCellsBody), new PropertyMetadata(null));
	}
}
