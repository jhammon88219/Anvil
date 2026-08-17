using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Anvil.Controls
{
	/// <summary>
	/// A "keep this window on top" pin for a card window's title-bar area (see PinToggle.xaml). Bind
	/// <see cref="IsChecked"/> two-way to the window's on-top VM flag; <c>CardWindowManager</c> applies the
	/// flag to the window's always-on-top presenter.
	/// </summary>
	public sealed partial class PinToggle : UserControl
	{
		public PinToggle()
		{
			InitializeComponent();
		}

		/// <summary>Whether the pin is engaged (window kept on top). Two-way bindable to the VM flag.</summary>
		public bool IsChecked
		{
			get => (bool)GetValue(IsCheckedProperty);
			set => SetValue(IsCheckedProperty, value);
		}

		public static readonly DependencyProperty IsCheckedProperty =
			DependencyProperty.Register(nameof(IsChecked), typeof(bool), typeof(PinToggle), new PropertyMetadata(false));
	}
}
