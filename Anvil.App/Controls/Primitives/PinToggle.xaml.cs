using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Anvil.Controls.Primitives
{
	/// <summary>
	/// A "keep this window above Anvil" pin for an app-wide window's title-bar area (see PinToggle.xaml). Bind
	/// <see cref="IsChecked"/> two-way to the window's on-top VM flag; <c>WindowManager</c> makes a pinned
	/// window an OWNED window of the main window (above Anvil only — not topmost over other apps).
	/// </summary>
	public sealed partial class PinToggle : UserControl
	{
		public PinToggle()
		{
			InitializeComponent();
		}

		/// <summary>Whether the pin is engaged (window kept above Anvil). Two-way bindable to the VM flag.</summary>
		public bool IsChecked
		{
			get => (bool)GetValue(IsCheckedProperty);
			set => SetValue(IsCheckedProperty, value);
		}

		public static readonly DependencyProperty IsCheckedProperty =
			DependencyProperty.Register(nameof(IsChecked), typeof(bool), typeof(PinToggle), new PropertyMetadata(false));
	}
}
