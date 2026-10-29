using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.TemporalPanes.DragMoveLayerExpanders
{
	/// <summary>
	/// ForeCast's Short Range section body (see the XAML header): the run card, Model / Product / Run / Tracks and the
	/// legend. Bound to <see cref="ShortRangeViewModel"/>; every decision is the VM's.
	/// </summary>
	public sealed partial class DragMoveLayerExpanderShortRangeBody : UserControl
	{
		public DragMoveLayerExpanderShortRangeBody()
		{
			InitializeComponent();
		}

		/// <summary>The view model; bound from the host (TemporalPaneForeCastContent → ViewModel.ShortRange).</summary>
		public ShortRangeViewModel ViewModel
		{
			get => (ShortRangeViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(ShortRangeViewModel), typeof(DragMoveLayerExpanderShortRangeBody), new PropertyMetadata(null));

		/// <summary>A hint line shows only while it has words.</summary>
		public Visibility HintVisibility(string hint) => string.IsNullOrEmpty(hint) ? Visibility.Collapsed : Visibility.Visible;
	}
}
