using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.Composites
{
	/// <summary>
	/// The Tropical section body (see the XAML header): the storm card and one row per product. Bound to
	/// <see cref="TropicalViewModel"/>; hosted by both the Now and Past windows. Every decision is the VM's.
	/// </summary>
	public sealed partial class TropicalInput : UserControl
	{
		public TropicalInput()
		{
			InitializeComponent();
		}

		/// <summary>x:Bind formatter for the counts.</summary>
		public static string Fmt(int count) => count.ToString(System.Globalization.CultureInfo.InvariantCulture);

		public Visibility HasText(string? value) => string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;

		/// <summary>The view model; bound from the host (NowCastTab / PastCastTab → ViewModel.Tropical).</summary>
		public TropicalViewModel ViewModel
		{
			get => (TropicalViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(TropicalViewModel), typeof(TropicalInput), new PropertyMetadata(null));
	}
}
