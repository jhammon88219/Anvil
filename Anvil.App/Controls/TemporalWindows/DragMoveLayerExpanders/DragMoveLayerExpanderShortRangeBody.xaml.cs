using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.TemporalWindows.DragMoveLayerExpanders
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

		/// <summary>The view model; bound from the host (TemporalWindowForeCastContent → ViewModel.ShortRange).</summary>
		public ShortRangeViewModel ViewModel
		{
			get => (ShortRangeViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(ShortRangeViewModel), typeof(DragMoveLayerExpanderShortRangeBody), new PropertyMetadata(null));
	}
}
