using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.Windows
{
	/// <summary>
	/// The NowCast settings panel (see the XAML header), hosted in its own OS window by
	/// <see cref="WindowManager"/> and driven by <see cref="MapViewModel.IsNowWindowOpen"/>. Bound to the
	/// coordinator <see cref="MapViewModel"/>.
	/// </summary>
	public sealed partial class NowCastWindow : UserControl
	{
		public NowCastWindow()
		{
			InitializeComponent();
		}

		/// <summary>x:Bind formatter for the per-type active-warning counts (int → display string). Used by
		/// the "Active" readout; re-evaluates when the bound count property raises PropertyChanged.</summary>
		public string Fmt(int count) => count.ToString(System.Globalization.CultureInfo.InvariantCulture);

		/// <summary>The coordinator view model; bound from the host.</summary>
		public MapViewModel ViewModel
		{
			get => (MapViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(NowCastWindow), new PropertyMetadata(null));
	}
}
