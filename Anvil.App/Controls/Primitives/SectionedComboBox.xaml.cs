using System;
using System.Collections;
using System.Linq;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Anvil.Controls.Primitives
{
	/// <summary>
	/// A dropdown with an optional pinned section above a scrolling main list (see the XAML header for the
	/// look and, more importantly, for why it is not a <c>ComboBox</c>).
	///
	/// Host contract: bind <see cref="ItemsSource"/> (and optionally <see cref="PinnedItemsSource"/>) and
	/// <see cref="SelectedItem"/> two-way; <see cref="PlaceholderText"/> shows when that is null. The closed
	/// face reads <c>SelectedItem.ToString()</c>.
	/// </summary>
	public sealed partial class SectionedComboBox : UserControl
	{
		// Rows of context kept above the selection when the main list opens scrolled to it.
		private const int RowsAboveSelection = 3;

		public SectionedComboBox()
		{
			InitializeComponent();
			// WinUI has no public Cursor property — ProtectedCursor is set from inside the control itself.
			ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
		}

		/// <summary>The fixed rows above the rule. Null or empty = no pinned section and no rule.</summary>
		public IEnumerable? PinnedItemsSource
		{
			get => (IEnumerable?)GetValue(PinnedItemsSourceProperty);
			set => SetValue(PinnedItemsSourceProperty, value);
		}

		public static readonly DependencyProperty PinnedItemsSourceProperty =
			DependencyProperty.Register(nameof(PinnedItemsSource), typeof(IEnumerable),
				typeof(SectionedComboBox), new PropertyMetadata(null));

		/// <summary>The scrolling rows below the rule.</summary>
		public IEnumerable? ItemsSource
		{
			get => (IEnumerable?)GetValue(ItemsSourceProperty);
			set => SetValue(ItemsSourceProperty, value);
		}

		public static readonly DependencyProperty ItemsSourceProperty =
			DependencyProperty.Register(nameof(ItemsSource), typeof(IEnumerable),
				typeof(SectionedComboBox), new PropertyMetadata(null));

		/// <summary>
		/// The selected row, from either section.
		/// </summary>
		/// <remarks>
		/// ⚠️ Keep it a TWO-WAY binding that accepts pushes from below: a host's selection can change without
		/// this control being touched (the isolation picker's map click is the everyday case), and the face +
		/// the next open both follow it.
		/// </remarks>
		public object? SelectedItem
		{
			get => GetValue(SelectedItemProperty);
			set => SetValue(SelectedItemProperty, value);
		}

		public static readonly DependencyProperty SelectedItemProperty =
			DependencyProperty.Register(nameof(SelectedItem), typeof(object),
				typeof(SectionedComboBox), new PropertyMetadata(null));

		/// <summary>Row template for BOTH sections; null shows each item's <c>ToString()</c>.</summary>
		public DataTemplate? ItemTemplate
		{
			get => (DataTemplate?)GetValue(ItemTemplateProperty);
			set => SetValue(ItemTemplateProperty, value);
		}

		public static readonly DependencyProperty ItemTemplateProperty =
			DependencyProperty.Register(nameof(ItemTemplate), typeof(DataTemplate),
				typeof(SectionedComboBox), new PropertyMetadata(null));

		/// <summary>Closed-state text while <see cref="SelectedItem"/> is null.</summary>
		public string PlaceholderText
		{
			get => (string)GetValue(PlaceholderTextProperty);
			set => SetValue(PlaceholderTextProperty, value);
		}

		public static readonly DependencyProperty PlaceholderTextProperty =
			DependencyProperty.Register(nameof(PlaceholderText), typeof(string),
				typeof(SectionedComboBox), new PropertyMetadata(string.Empty));

		/// <summary>Cap on the MAIN list's height (the pinned section is always shown whole).</summary>
		public double MaxDropDownHeight
		{
			get => (double)GetValue(MaxDropDownHeightProperty);
			set => SetValue(MaxDropDownHeightProperty, value);
		}

		public static readonly DependencyProperty MaxDropDownHeightProperty =
			DependencyProperty.Register(nameof(MaxDropDownHeight), typeof(double),
				typeof(SectionedComboBox), new PropertyMetadata(360d));

		/// <summary>Optional template for the CLOSED face; null shows <c>SelectedItem.ToString()</c>.</summary>
		public DataTemplate? FaceTemplate
		{
			get => (DataTemplate?)GetValue(FaceTemplateProperty);
			set => SetValue(FaceTemplateProperty, value);
		}

		public static readonly DependencyProperty FaceTemplateProperty =
			DependencyProperty.Register(nameof(FaceTemplate), typeof(DataTemplate),
				typeof(SectionedComboBox), new PropertyMetadata(null));

		/// <summary>Optional host content under both sections, below its own rule. Never selectable; with it
		/// set the dropdown opens even when both lists are empty (the footer can explain why).</summary>
		public object? FooterContent
		{
			get => GetValue(FooterContentProperty);
			set => SetValue(FooterContentProperty, value);
		}

		public static readonly DependencyProperty FooterContentProperty =
			DependencyProperty.Register(nameof(FooterContent), typeof(object),
				typeof(SectionedComboBox), new PropertyMetadata(null));

		/// <summary>A row was clicked — raised on EVERY click, including one on the row already selected
		/// (which changes no property, so no binding hears it). Raised after <see cref="SelectedItem"/> is set.</summary>
		public event EventHandler<object>? ItemPicked;

		/// <summary>Which side of the box the dropdown opens on. Top by default: the strips sit at the
		/// bottom of the window.</summary>
		public FlyoutPlacementMode DropDownPlacement
		{
			get => (FlyoutPlacementMode)GetValue(DropDownPlacementProperty);
			set => SetValue(DropDownPlacementProperty, value);
		}

		public static readonly DependencyProperty DropDownPlacementProperty =
			DependencyProperty.Register(nameof(DropDownPlacement), typeof(FlyoutPlacementMode),
				typeof(SectionedComboBox), new PropertyMetadata(FlyoutPlacementMode.Top));

		// ── Closed-state face ────────────────────────────────────────────────────────────────────
		public string LabelOf(object? item) => item?.ToString() ?? string.Empty;

		public Visibility ShowsLabel(object? item, DataTemplate? face) =>
			item is null || face is not null ? Visibility.Collapsed : Visibility.Visible;

		public Visibility ShowsFace(object? item, DataTemplate? face) =>
			item is null || face is null ? Visibility.Collapsed : Visibility.Visible;

		public Visibility NoSelection(object? item) =>
			item is null ? Visibility.Visible : Visibility.Collapsed;

		// ── Dropdown ─────────────────────────────────────────────────────────────────────────────
		private void OnTapped(object sender, TappedRoutedEventArgs e) => OpenDropDown();

		// Keyboard parity with a combo box: Space/Enter opens it. Inside, arrows move FOCUS only and
		// Enter/Space on a row picks it (ItemClick).
		private void OnKeyDown(object sender, KeyRoutedEventArgs e)
		{
			if (e.Key is not (VirtualKey.Space or VirtualKey.Enter)) { return; }
			OpenDropDown();
			e.Handled = true;
		}

		private void OpenDropDown()
		{
			if (!IsEnabled || (!HasAny(PinnedItemsSource) && !HasAny(ItemsSource) && FooterContent is null)) { return; }
			DropDown.ShowAt(Root, new FlyoutShowOptions { Placement = DropDownPlacement });
		}

		// Size to the box, show only the sections that have rows, and highlight the current row in
		// whichever section holds it. Evaluated per open, so a live collection is picked up too.
		private void OnDropDownOpening(object? sender, object e)
		{
			DropDownPanel.Width = Math.Max(0, Root.ActualWidth - 2);   // less the presenter's 1px border each side

			bool pinned = HasAny(PinnedItemsSource);
			bool main = HasAny(ItemsSource);
			PinnedList.Visibility = pinned ? Visibility.Visible : Visibility.Collapsed;
			MainList.Visibility = main ? Visibility.Visible : Visibility.Collapsed;
			SectionRule.Visibility = pinned && main ? Visibility.Visible : Visibility.Collapsed;
			bool footer = FooterContent is not null;
			Footer.Visibility = footer ? Visibility.Visible : Visibility.Collapsed;
			FooterRule.Visibility = footer && (pinned || main) ? Visibility.Visible : Visibility.Collapsed;

			// Pinned wins if one instance sits in both lists, so only one row is ever lit.
			bool inPinned = Contains(PinnedItemsSource, SelectedItem);
			PinnedList.SelectedItem = inPinned ? SelectedItem : null;
			MainList.SelectedItem = !inPinned && Contains(ItemsSource, SelectedItem) ? SelectedItem : null;
		}

		// After layout exists: bring a main-list selection into view (a few rows of context above it) and
		// put keyboard focus on the lit row, so arrows start from where you are.
		private void OnDropDownOpened(object? sender, object e)
		{
			if (MainList.SelectedItem is { } selected)
			{
				int index = MainList.Items.IndexOf(selected);
				MainList.ScrollIntoView(MainList.Items[Math.Max(0, index - RowsAboveSelection)], ScrollIntoViewAlignment.Leading);
				MainList.UpdateLayout();
				FocusRow(MainList, index);
			}
			else if (PinnedList.SelectedItem is { } pinned)
			{
				FocusRow(PinnedList, PinnedList.Items.IndexOf(pinned));
			}
		}

		// ⚠️ THE ONE WRITER OF SelectedItem (see the header). A pointer click or Enter/Space on a row.
		private void OnItemClick(object sender, ItemClickEventArgs e)
		{
			SelectedItem = e.ClickedItem;
			DropDown.Hide();
			ItemPicked?.Invoke(this, e.ClickedItem);
		}

		/// <summary>Close the dropdown — for a <see cref="FooterContent"/> action.</summary>
		public void CloseDropDown() => DropDown.Hide();

		// ── Keyboard across the rule ─────────────────────────────────────────────────────────────
		// Two lists don't hand focus to each other on their own: Down off the last pinned row enters the
		// main list, Up off its first row returns.
		private void OnPinnedListPreviewKeyDown(object sender, KeyRoutedEventArgs e)
		{
			if (e.Key != VirtualKey.Down || MainList.Visibility != Visibility.Visible) { return; }
			if (FocusedIndex(PinnedList) != PinnedList.Items.Count - 1) { return; }
			e.Handled = FocusRow(MainList, 0);
		}

		private void OnMainListPreviewKeyDown(object sender, KeyRoutedEventArgs e)
		{
			if (e.Key != VirtualKey.Up || PinnedList.Visibility != Visibility.Visible) { return; }
			if (FocusedIndex(MainList) != 0) { return; }
			e.Handled = FocusRow(PinnedList, PinnedList.Items.Count - 1);
		}

		private int FocusedIndex(ListView list) =>
			FocusManager.GetFocusedElement(XamlRoot) is ListViewItem row ? list.IndexFromContainer(row) : -1;

		private static bool FocusRow(ListView list, int index)
		{
			if (index < 0 || index >= list.Items.Count) { return false; }
			list.ScrollIntoView(list.Items[index]);
			list.UpdateLayout();   // realize the container before asking for it (the main list virtualizes)
			return list.ContainerFromIndex(index) is ListViewItem row && row.Focus(FocusState.Keyboard);
		}

		private static bool HasAny(IEnumerable? source) => source switch
		{
			null => false,
			ICollection collection => collection.Count > 0,
			_ => source.GetEnumerator().MoveNext(),
		};

		private static bool Contains(IEnumerable? source, object? item) =>
			item is not null && source is not null && source.Cast<object>().Contains(item);

		// ── Visual states ────────────────────────────────────────────────────────────────────────
		private void OnIsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
		{
			if (!IsEnabled) { DropDown.Hide(); }
			VisualStateManager.GoToState(this, IsEnabled ? "Normal" : "Disabled", true);
		}
	}
}
