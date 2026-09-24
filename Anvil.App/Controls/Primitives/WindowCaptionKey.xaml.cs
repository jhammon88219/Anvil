using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Anvil.Controls.Primitives
{
	/// <summary>
	/// The main window's minimize / maximize-restore / close as one segmented bar key (see
	/// WindowCaptionKey.xaml). It only RAISES the three requests; the host drives the window, and pushes
	/// <see cref="IsMaximized"/> back so the middle segment shows Maximize or Restore.
	/// </summary>
	public sealed partial class WindowCaptionKey : UserControl
	{
		public WindowCaptionKey()
		{
			InitializeComponent();
		}

		public event EventHandler? MinimizeRequested;
		public event EventHandler? MaximizeRestoreRequested;
		public event EventHandler? CloseRequested;

		/// <summary>Whether the host window is maximized — flips the middle segment to Restore.</summary>
		public bool IsMaximized
		{
			get => (bool)GetValue(IsMaximizedProperty);
			set => SetValue(IsMaximizedProperty, value);
		}

		public static readonly DependencyProperty IsMaximizedProperty =
			DependencyProperty.Register(nameof(IsMaximized), typeof(bool), typeof(WindowCaptionKey), new PropertyMetadata(false));

		// x:Bind functions: the system's own ChromeMaximize / ChromeRestore glyphs and names.
		public string MaximizeGlyph(bool maximized) => maximized ? "" : "";

		public string MaximizeLabel(bool maximized) => maximized ? "Restore down" : "Maximize";

		private void OnMinimizeClick(object sender, RoutedEventArgs e) => MinimizeRequested?.Invoke(this, EventArgs.Empty);

		private void OnMaximizeRestoreClick(object sender, RoutedEventArgs e) => MaximizeRestoreRequested?.Invoke(this, EventArgs.Empty);

		private void OnCloseClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
	}
}
