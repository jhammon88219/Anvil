using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Anvil.Services;

namespace Anvil.Controls.Primitives
{
	/// <summary>
	/// What hosts the Settings rows (SettingsWindow): told when a row is picked, so it can lift that row and fill the page's
	/// About box. A row finds its host by walking up the visual tree — the consoles never pass anything down.
	/// </summary>
	public interface ISettingsRowHost
	{
		void OnRowPicked(ISettingsPickableRow row);

		/// <summary>A row's About text changed while it may be showing (an About that follows the pick).</summary>
		void OnRowWordsChanged(ISettingsPickableRow row);
	}

	/// <summary>
	/// Anything the page's About box can show: a <see cref="SettingsConsoleRow"/>, or a row with its own grid that still
	/// lifts and picks the same way (Storage's On disk table rows). Find the host with <see cref="Pick"/>.
	/// </summary>
	public interface ISettingsPickableRow
	{
		/// <summary>Its entry in Services/Settings/SettingsText — also how "Find a setting" finds the row to land on.</summary>
		string? AboutKey { get; }
		string AboutTitle { get; }
		string AboutText { get; }
		bool IsSelected { get; set; }

		/// <summary>The "you landed here" accent ring: two pulses then a slow fade (~2 s) — after a search pick.</summary>
		void Flash();
	}

	public static class SettingsRowPicking
	{
		/// <summary>Windows' Settings → Accessibility → Visual effects → Animation effects (read at each flash).</summary>
		public static bool AnimationsOn => new global::Windows.UI.ViewManagement.UISettings().AnimationsEnabled;

		/// <summary>Walk up to the <see cref="ISettingsRowHost"/> and report <paramref name="row"/> picked.</summary>
		public static void Pick(FrameworkElement row)
		{
			if (row is ISettingsPickableRow pickable && HostOf(row) is { } host) { host.OnRowPicked(pickable); }
		}

		/// <summary>Tell the host <paramref name="row"/>'s About text changed (so a showing About box re-reads it).</summary>
		public static void WordsChanged(FrameworkElement row)
		{
			if (row is ISettingsPickableRow pickable && HostOf(row) is { } host) { host.OnRowWordsChanged(pickable); }
		}

		private static ISettingsRowHost? HostOf(FrameworkElement row)
		{
			for (DependencyObject? node = VisualTreeHelper.GetParent(row); node is not null; node = VisualTreeHelper.GetParent(node))
			{
				if (node is ISettingsRowHost host) { return host; }
			}
			return null;
		}
	}

	/// <summary>
	/// One setting on a console (see the XAML header): the four cells on the shared widths, plus the hover and selected lifts.
	/// The row's content is its Control cell.
	/// </summary>
	[ContentProperty(Name = nameof(ControlContent))]
	public sealed partial class SettingsConsoleRow : UserControl, ISettingsPickableRow
	{
		public SettingsConsoleRow()
		{
			InitializeComponent();
			// A click on the row's OWN control (a slider, a box) is handled by that control — listen to handled presses too,
			// so clicking the control still picks the row.
			AddHandler(PointerPressedEvent, new PointerEventHandler(OnAnyPointerPressed), handledEventsToo: true);
			GotFocus += (_, _) => Pick();   // keyboard: tabbing into a row's control picks it
			Loaded += (_, _) => ApplyStates(false);   // GoToState before the tree is up can be a no-op
		}

		// ── The cells ──

		/// <summary>The Label column's name (empty for a checkbox row — its box carries the words).</summary>
		public string Label
		{
			get => (string)GetValue(LabelProperty);
			set => SetValue(LabelProperty, value);
		}
		public static readonly DependencyProperty LabelProperty =
			DependencyProperty.Register(nameof(Label), typeof(string), typeof(SettingsConsoleRow), new PropertyMetadata(string.Empty));

		/// <summary>The Control column: the row's control (or a small panel of them).</summary>
		public object? ControlContent
		{
			get => GetValue(ControlContentProperty);
			set => SetValue(ControlContentProperty, value);
		}
		public static readonly DependencyProperty ControlContentProperty =
			DependencyProperty.Register(nameof(ControlContent), typeof(object), typeof(SettingsConsoleRow), new PropertyMetadata(null));

		/// <summary>How many columns the Control cell spans (1 = its own; 2 = on over the Hint; 3 = to the row's end).</summary>
		public int ControlSpan
		{
			get => (int)GetValue(ControlSpanProperty);
			set => SetValue(ControlSpanProperty, value);
		}
		public static readonly DependencyProperty ControlSpanProperty =
			DependencyProperty.Register(nameof(ControlSpan), typeof(int), typeof(SettingsConsoleRow), new PropertyMetadata(1));

		/// <summary>An explicit hint (one that follows state). Null = the catalog's hint for <see cref="AboutKey"/>.</summary>
		public string? Hint
		{
			get => (string?)GetValue(HintProperty);
			set => SetValue(HintProperty, value);
		}
		public static readonly DependencyProperty HintProperty =
			DependencyProperty.Register(nameof(Hint), typeof(string), typeof(SettingsConsoleRow), new PropertyMetadata(null));

		/// <summary>A custom Hint cell (several readouts, a link) instead of the text.</summary>
		public object? HintContent
		{
			get => GetValue(HintContentProperty);
			set => SetValue(HintContentProperty, value);
		}
		public static readonly DependencyProperty HintContentProperty =
			DependencyProperty.Register(nameof(HintContent), typeof(object), typeof(SettingsConsoleRow), new PropertyMetadata(null));

		/// <summary>The hint may run on into the Default column (a row with no default and a long message).</summary>
		public bool HintSpansDefault
		{
			get => (bool)GetValue(HintSpansDefaultProperty);
			set => SetValue(HintSpansDefaultProperty, value);
		}
		public static readonly DependencyProperty HintSpansDefaultProperty =
			DependencyProperty.Register(nameof(HintSpansDefault), typeof(bool), typeof(SettingsConsoleRow), new PropertyMetadata(false));

		/// <summary>The Default column's text: what Restore defaults writes.</summary>
		public string Default
		{
			get => (string)GetValue(DefaultProperty);
			set => SetValue(DefaultProperty, value);
		}
		public static readonly DependencyProperty DefaultProperty =
			DependencyProperty.Register(nameof(Default), typeof(string), typeof(SettingsConsoleRow), new PropertyMetadata(string.Empty));

		/// <summary>A custom Default cell (a swatch chip + text).</summary>
		public object? DefaultContent
		{
			get => GetValue(DefaultContentProperty);
			set => SetValue(DefaultContentProperty, value);
		}
		public static readonly DependencyProperty DefaultContentProperty =
			DependencyProperty.Register(nameof(DefaultContent), typeof(object), typeof(SettingsConsoleRow), new PropertyMetadata(null));

		/// <summary>Top for a tall control (a checklist, a file list): the label, hint and default sit on its first line.</summary>
		public VerticalAlignment TextVerticalAlignment
		{
			get => (VerticalAlignment)GetValue(TextVerticalAlignmentProperty);
			set => SetValue(TextVerticalAlignmentProperty, value);
		}
		public static readonly DependencyProperty TextVerticalAlignmentProperty =
			DependencyProperty.Register(nameof(TextVerticalAlignment), typeof(VerticalAlignment), typeof(SettingsConsoleRow),
				new PropertyMetadata(VerticalAlignment.Center));

		/// <summary>Dim the label (a setting that can't be used yet).</summary>
		public bool IsLabelDimmed
		{
			get => (bool)GetValue(IsLabelDimmedProperty);
			set => SetValue(IsLabelDimmedProperty, value);
		}
		public static readonly DependencyProperty IsLabelDimmedProperty =
			DependencyProperty.Register(nameof(IsLabelDimmed), typeof(bool), typeof(SettingsConsoleRow),
				new PropertyMetadata(false, (d, _) => ((SettingsConsoleRow)d).ApplyStates(true)));

		// ── The words + selection ──

		/// <summary>The row's entry in <see cref="SettingsText"/>: its hint, and the About box's title + text.</summary>
		public string? AboutKey
		{
			get => (string?)GetValue(AboutKeyProperty);
			set => SetValue(AboutKeyProperty, value);
		}
		public static readonly DependencyProperty AboutKeyProperty =
			DependencyProperty.Register(nameof(AboutKey), typeof(string), typeof(SettingsConsoleRow), new PropertyMetadata(null));

		/// <summary>The row the page's About box shows. Set by the host only.</summary>
		public bool IsSelected
		{
			get => (bool)GetValue(IsSelectedProperty);
			set => SetValue(IsSelectedProperty, value);
		}
		public static readonly DependencyProperty IsSelectedProperty =
			DependencyProperty.Register(nameof(IsSelected), typeof(bool), typeof(SettingsConsoleRow),
				new PropertyMetadata(false, (d, _) => ((SettingsConsoleRow)d).ApplyStates(true)));

		/// <summary>The About box's title: the row's own label (it can be data — a feed's name), else the catalog's (a checkbox
		/// row has no label; its title is the box's text).</summary>
		public string AboutTitle => !string.IsNullOrEmpty(Label) ? Label : SettingsText.Get(AboutKey)?.Title ?? string.Empty;

		/// <summary>An explicit About text (one that follows the pick, e.g. Live Data's source — its words still come from
		/// SettingsText). Null = the catalog's About for <see cref="AboutKey"/>.</summary>
		public string? About
		{
			get => (string?)GetValue(AboutProperty);
			set => SetValue(AboutProperty, value);
		}
		public static readonly DependencyProperty AboutProperty =
			DependencyProperty.Register(nameof(About), typeof(string), typeof(SettingsConsoleRow),
				new PropertyMetadata(null, (d, _) => SettingsRowPicking.WordsChanged((SettingsConsoleRow)d)));

		/// <summary>The About box's text ("" when the row has no catalog entry).</summary>
		public string AboutText => About ?? SettingsText.Get(AboutKey)?.About ?? string.Empty;

		// ── x:Bind helpers ──

		public string HintText(string? hint, string? aboutKey) => hint ?? SettingsText.Get(aboutKey)?.Hint ?? string.Empty;

		public int HintSpan(bool spans) => spans ? 2 : 1;

		/// <summary>A custom cell replaces the plain text (the text still feeds the About box).</summary>
		public Visibility NoContent(object? content) => content is null ? Visibility.Visible : Visibility.Collapsed;

		public Thickness TopInset(VerticalAlignment alignment) =>
			alignment == VerticalAlignment.Top ? new Thickness(0, 6, 0, 0) : new Thickness(0);

		/// <summary>The found ring: the pulses, or a still ring while Windows' Animation effects are off.</summary>
		public void Flash() => (SettingsRowPicking.AnimationsOn ? SettingsConsoleRowFoundFlash : SettingsConsoleRowFoundHold).Begin();

		// ── States ──

		private bool _hovered;

		private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
		{
			_hovered = true;
			ApplyStates(true);
		}

		private void OnPointerExited(object sender, PointerRoutedEventArgs e)
		{
			_hovered = false;
			ApplyStates(true);
		}

		private void ApplyStates(bool useTransitions)
		{
			VisualStateManager.GoToState(this, _hovered && IsPickable ? "Hovered" : "NotHovered", useTransitions);
			VisualStateManager.GoToState(this, IsSelected ? "Selected" : "Unselected", useTransitions);
			VisualStateManager.GoToState(this, IsLabelDimmed ? "LabelDimmed" : "LabelNormal", false);
		}

		// ── Picking ──

		private void OnAnyPointerPressed(object sender, PointerRoutedEventArgs e) => Pick();

		/// <summary>Only a row with words to show lifts and can be picked (a status line has no AboutKey).</summary>
		private bool IsPickable => !string.IsNullOrEmpty(AboutKey);

		private void Pick()
		{
			if (!IsPickable) { return; }
			SettingsRowPicking.Pick(this);
		}
	}
}
