using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Anvil.Controls.Primitives;
using Anvil.Models;
using Anvil.Services;
using Anvil.ViewModels;

namespace Anvil.Controls.StorageManagers
{
	/// <summary>
	/// One row of Settings → Storage's table (see the XAML header): a <see cref="StorageItemViewModel"/> plus its
	/// kind colour. Code-behind only relays clicks and keeps the Size colour's VisualState on <see cref="StorageItemViewModel.Fill"/>.
	/// </summary>
	public sealed partial class StorageManagerSettingsConsoleDataTableRow : UserControl, ISettingsPickableRow
	{
		public StorageManagerSettingsConsoleDataTableRow()
		{
			InitializeComponent();
			// A click on the row's own button / dropdown is handled by it — listen to handled presses too, so it still picks.
			AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => SettingsRowPicking.Pick(this)), handledEventsToo: true);
			GotFocus += (_, _) => SettingsRowPicking.Pick(this);
			// GoToState before the first layout can be lost — re-apply once loaded.
			Loaded += (_, _) =>
			{
				ApplyFill(false);
				ApplyLifts(false);
			};
		}

		// ── PICKING (the page's About box) ──

		/// <summary>The kind's entry in <see cref="SettingsText"/> ("storage.&lt;kind&gt;").</summary>
		public string? AboutKey
		{
			get => (string?)GetValue(AboutKeyProperty);
			set => SetValue(AboutKeyProperty, value);
		}

		public static readonly DependencyProperty AboutKeyProperty =
			DependencyProperty.Register(nameof(AboutKey), typeof(string), typeof(StorageManagerSettingsConsoleDataTableRow), new PropertyMetadata(null));

		/// <summary>The row the page's About box shows. Set by the host only.</summary>
		public bool IsSelected
		{
			get => (bool)GetValue(IsSelectedProperty);
			set => SetValue(IsSelectedProperty, value);
		}

		public static readonly DependencyProperty IsSelectedProperty =
			DependencyProperty.Register(nameof(IsSelected), typeof(bool), typeof(StorageManagerSettingsConsoleDataTableRow),
				new PropertyMetadata(false, (d, _) => ((StorageManagerSettingsConsoleDataTableRow)d).ApplyLifts(true)));

		public string AboutTitle => Item?.Name ?? SettingsText.Get(AboutKey)?.Title ?? string.Empty;

		public string AboutText => SettingsText.Get(AboutKey)?.About ?? string.Empty;

		public string Default => Item?.DefaultText ?? string.Empty;

		/// <summary>The found ring (DataTableRowFoundFlash in the XAML).</summary>
		public void Flash() => DataTableRowFoundFlash.Begin();

		private bool _hovered;

		private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
		{
			_hovered = true;
			ApplyLifts(true);
		}

		private void OnPointerExited(object sender, PointerRoutedEventArgs e)
		{
			_hovered = false;
			ApplyLifts(true);
		}

		private void ApplyLifts(bool useTransitions)
		{
			VisualStateManager.GoToState(this, _hovered ? "Hovered" : "NotHovered", useTransitions);
			VisualStateManager.GoToState(this, IsSelected ? "Selected" : "Unselected", useTransitions);
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

		/// <summary>A Clear button: only when the kind can be cleared (not Maps), the plain or the asking one.</summary>
		public Visibility ShowClear(bool hasClear, bool confirm, bool wantConfirm) =>
			hasClear && confirm == wantConfirm ? Visibility.Visible : Visibility.Collapsed;

		/// <summary>Keep for with no dropdown: its words ("Always kept"), or a hairline when there are none (Maps).</summary>
		public Visibility ShowNoKeep(bool hasKeep, string text, bool asText) =>
			!hasKeep && string.IsNullOrEmpty(text) != asText ? Visibility.Visible : Visibility.Collapsed;

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
