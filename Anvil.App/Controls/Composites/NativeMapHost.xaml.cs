using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NativeMaps.WinUI;

namespace Anvil.Controls.Composites
{
	/// <summary>NATIVE-MAP (bolt-on): the NativeMaps view's slot over the WebView map (see the XAML header). Builds the view
	/// while <see cref="IsActive"/> and drops it otherwise; the hand-over work is MainWindow's.</summary>
	public sealed partial class NativeMapHost : UserControl
	{
		public NativeMapHost()
		{
			InitializeComponent();
		}

		/// <summary>True while NativeMaps is the picked engine: x:Load builds the view.</summary>
		public bool IsActive
		{
			get => (bool)GetValue(IsActiveProperty);
			set => SetValue(IsActiveProperty, value);
		}

		public static readonly DependencyProperty IsActiveProperty =
			DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(NativeMapHost), new PropertyMetadata(false));

		/// <summary>The NativeMaps view while it is built, otherwise null.</summary>
		public MapView? View => NativeMapView;

		/// <summary>The view was built and loaded. Sender is the <see cref="MapView"/>.</summary>
		public event RoutedEventHandler? MapViewLoaded;

		/// <summary>The view is being dropped. Sender is the <see cref="MapView"/>.</summary>
		public event RoutedEventHandler? MapViewUnloaded;

		private void OnNativeMapViewLoaded(object sender, RoutedEventArgs e) => MapViewLoaded?.Invoke(sender, e);

		private void OnNativeMapViewUnloaded(object sender, RoutedEventArgs e) => MapViewUnloaded?.Invoke(sender, e);
	}
}
