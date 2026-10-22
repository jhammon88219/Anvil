using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.Models;

namespace Anvil.Controls.Primitives
{
	/// <summary>
	/// One square key on the tools tier whose upward list picks the pane layout (see
	/// MapControlsStripPaneLayoutPicker.xaml). <see cref="Layout"/> is ONE-WAY from the host; a pick raises
	/// <see cref="LayoutPicked"/> and the host sets the VM, then re-asserts <see cref="Layout"/>.
	/// </summary>
	public sealed partial class MapControlsStripPaneLayoutPicker : UserControl
	{
		private bool _isFlyoutOpen;

		public MapControlsStripPaneLayoutPicker()
		{
			InitializeComponent();
			// GoToState before Loaded can be a no-op — re-apply once the tree is up.
			Loaded += (_, _) => ApplyStates();
			IsEnabledChanged += (_, _) => ApplyStates();
		}

		/// <summary>The layout the map is in now (the face's mark, the dotted row).</summary>
		public PaneLayout Layout
		{
			get => (PaneLayout)GetValue(LayoutProperty);
			set => SetValue(LayoutProperty, value);
		}

		public static readonly DependencyProperty LayoutProperty =
			DependencyProperty.Register(nameof(Layout), typeof(PaneLayout), typeof(MapControlsStripPaneLayoutPicker),
				new PropertyMetadata(PaneLayout.Single));

		/// <summary>A row was clicked (the current layout's included).</summary>
		public event EventHandler<PaneLayout>? LayoutPicked;

		// x:Bind can't compare against an enum literal, so the XAML passes the member's ordinal.
		public bool IsLayout(PaneLayout layout, int ordinal) => (int)layout == ordinal;

		public Visibility MarkVisibility(PaneLayout layout, int ordinal) =>
			IsLayout(layout, ordinal) ? Visibility.Visible : Visibility.Collapsed;

		// ⚠️ Re-assert all three rows after the host has answered: clicking the dotted row changes no property, and a
		// refused pick would leave the clicked row dotted.
		private void OnRowClick(object sender, RoutedEventArgs e)
		{
			if (sender is not FrameworkElement { Tag: string tag } || !Enum.TryParse<PaneLayout>(tag, out var layout))
			{
				return;
			}
			LayoutPicked?.Invoke(this, layout);
			PaneLayoutPickerSingleRadioButton.IsChecked = IsLayout(Layout, 0);
			PaneLayoutPickerTwoRadioButton.IsChecked = IsLayout(Layout, 1);
			PaneLayoutPickerQuadRadioButton.IsChecked = IsLayout(Layout, 2);
			PaneLayoutPickerFlyout.Hide(); // one choice: the pick closes the list
		}

		private void OnFlyoutOpened(object? sender, object e)
		{
			_isFlyoutOpen = true;
			ApplyStates();
		}

		private void OnFlyoutClosed(object? sender, object e)
		{
			_isFlyoutOpen = false;
			ApplyStates();
		}

		// ONE place both state groups land.
		private void ApplyStates()
		{
			VisualStateManager.GoToState(this, _isFlyoutOpen ? "FlyoutOpen" : "FlyoutClosed", false);
			VisualStateManager.GoToState(this, IsEnabled ? "Enabled" : "Disabled", false);
		}
	}
}
