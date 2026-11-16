using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Anvil.Models;
using Anvil.ViewModels;

namespace Anvil.Controls.StorageManagers
{
	/// <summary>
	/// One row of Settings → Storage's table (see the XAML header): a <see cref="StorageItemViewModel"/> plus its
	/// kind colour. Code-behind only relays clicks and keeps the Size colour's VisualState on <see cref="StorageItemViewModel.Fill"/>.
	/// </summary>
	public sealed partial class StorageManagerSettingsConsoleDataTableRow : UserControl
	{
		public StorageManagerSettingsConsoleDataTableRow()
		{
			InitializeComponent();
			// GoToState before the first layout can be lost — re-apply once loaded.
			Loaded += (_, _) => ApplyFill(false);
		}

		/// <summary>The row's data.</summary>
		public StorageItemViewModel Item
		{
			get => (StorageItemViewModel)GetValue(ItemProperty);
			set => SetValue(ItemProperty, value);
		}

		public static readonly DependencyProperty ItemProperty =
			DependencyProperty.Register(nameof(Item), typeof(StorageItemViewModel), typeof(StorageManagerSettingsConsoleDataTableRow),
				new PropertyMetadata(null, OnItemChanged));

		/// <summary>The kind's DATA colour (Styles.xaml <c>Storage*Brush</c>), same as its bar segment.</summary>
		public Brush Swatch
		{
			get => (Brush)GetValue(SwatchProperty);
			set => SetValue(SwatchProperty, value);
		}

		public static readonly DependencyProperty SwatchProperty =
			DependencyProperty.Register(nameof(Swatch), typeof(Brush), typeof(StorageManagerSettingsConsoleDataTableRow),
				new PropertyMetadata(null));

		public Visibility Show(bool value, bool when) => value == when ? Visibility.Visible : Visibility.Collapsed;

		/// <summary>The name's own width while its column is still Auto (0 before the first layout).</summary>
		internal double NaturalNameWidth => KindName.ActualWidth;

		/// <summary>The table fixes every row's name column to the widest name, so the grey hints line up.</summary>
		internal void SetNameColumnWidth(double width) => NameColumn.Width = new GridLength(width);

		private static void OnItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
		{
			var row = (StorageManagerSettingsConsoleDataTableRow)d;
			if (e.OldValue is StorageItemViewModel old) old.PropertyChanged -= row.OnItemPropertyChanged;
			if (e.NewValue is StorageItemViewModel now) now.PropertyChanged += row.OnItemPropertyChanged;
			row.ApplyFill(false);
		}

		private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName == nameof(StorageItemViewModel.Fill)) ApplyFill(true);
		}

		private void ApplyFill(bool animate)
		{
			var state = (Item?.Fill ?? StorageFill.Low) switch
			{
				StorageFill.High => "High",
				StorageFill.Medium => "Medium",
				_ => "Low",
			};
			VisualStateManager.GoToState(this, state, animate);
		}

		private async void OnClearClick(object sender, RoutedEventArgs e)
		{
			if (Item is not null) await Item.ClearAsync();
		}

		private async void OnClearConfirmedClick(object sender, RoutedEventArgs e)
		{
			ClearConfirmFlyout.Hide();
			if (Item is not null) await Item.ClearAsync();
		}

		private void OnOpenFolderClick(object sender, RoutedEventArgs e)
		{
			if (Item is not null) StorageManagerFolderLauncher.Open(Item.Folder);
		}
	}
}
