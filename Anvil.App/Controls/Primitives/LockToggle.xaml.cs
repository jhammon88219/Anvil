using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Anvil.Controls.Primitives
{
	/// <summary>
	/// A "lock this window in place" toggle for an app-wide window's title-bar area, beside the pin (see
	/// LockToggle.xaml). Bind <see cref="IsChecked"/> two-way to the window's lock VM flag;
	/// <c>WindowManager</c> refuses moves and resizes while it is set.
	/// </summary>
	public sealed partial class LockToggle : UserControl
	{
		public LockToggle()
		{
			InitializeComponent();
		}

		/// <summary>Whether the window is locked (no move, no resize). Two-way bindable to the VM flag.</summary>
		public bool IsChecked
		{
			get => (bool)GetValue(IsCheckedProperty);
			set => SetValue(IsCheckedProperty, value);
		}

		public static readonly DependencyProperty IsCheckedProperty =
			DependencyProperty.Register(nameof(IsChecked), typeof(bool), typeof(LockToggle), new PropertyMetadata(true));

		// x:Bind functions: closed padlock while locked, open while not.
		public string GlyphFor(bool locked) => locked ? "" : "";

		public string ToolTipFor(bool locked) =>
			locked ? "Locked in place — click to allow moving and resizing" : "Unlocked — click to lock in place";
	}
}
