using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Anvil.Models;
using Anvil.ViewModels;

namespace Anvil.Controls.Composites
{
	/// <summary>
	/// The place search box, centred on the top menu bar (moved off the tools strip, 2026-11-03). See the XAML header for
	/// the rules (offline rows, online on Enter only, the pinned ✕).
	/// </summary>
	public sealed partial class PlaceSearchBox : UserControl
	{
		public PlaceSearchBox()
		{
			InitializeComponent();
		}

		/// <summary>The coordinator view model; bound from the host.</summary>
		public MapViewModel ViewModel
		{
			get => (MapViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(PlaceSearchBox), new PropertyMetadata(null));

		// Greyed while the PastCast loading screen is up (RadarViewModel.AreControlsLocked), like every map tool.
		public bool Free(bool locked) => !locked;

		// Only the USER's typing refreshes rows — arrowing through the list (SuggestionChosen) and our own Text writes
		// (ProgrammaticChange) must not, or picking a row would re-query as you move.
		private void OnPlaceSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
		{
			PinClearButton();
			if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
			{
				ViewModel?.PlaceSearch.UpdateSuggestions(sender.Text);
			}
		}

		// ===== The box's X stays while there is text, focused or not =====
		// The X is the inner TextBox's own template part "DeleteButton" (declared Visibility="Collapsed"). The
		// TextBox shows it only while FOCUSED: its ButtonVisible state animates it to Visible, and ButtonCollapsed
		// has NO storyboard, so in that state the element's own value shows. Setting that value here, from the
		// text, keeps the X up after focus leaves (clicking the map) — so coming back to clear it is ONE click.
		// ⚠️ Rests on the WinUI template (AutoSuggestBoxTextBoxStyle, checked against WinAppSDK 2.1 generic.xaml):
		// if a future template gives ButtonCollapsed a storyboard, this quietly reverts to focus-only.
		private Button? _clearButton;

		private void OnPlaceSearchLoaded(object sender, RoutedEventArgs e) => PinClearButton();

		// Looks the part up lazily (Loaded, then every text change) in case the inner template lands late.
		private void PinClearButton()
		{
			if (_clearButton is null && FindNamed<Button>(PlaceSearchBoxInput, "DeleteButton") is { } button)
			{
				_clearButton = button;
				// Belt and braces: the TextBox's own handler clears the text too, but an UNFOCUSED click must not
				// depend on it — clear here and say so to the VM (the empty box is what removes the pin).
				_clearButton.Click += OnClearSearchClick;
			}
			if (_clearButton is not null)
			{
				_clearButton.Visibility = string.IsNullOrEmpty(PlaceSearchBoxInput.Text) ? Visibility.Collapsed : Visibility.Visible;
			}
		}

		private void OnClearSearchClick(object sender, RoutedEventArgs e)
		{
			if (PlaceSearchBoxInput.Text.Length > 0)
			{
				PlaceSearchBoxInput.Text = string.Empty;
			}
			ViewModel?.PlaceSearch.UpdateSuggestions(string.Empty);
		}

		private static T? FindNamed<T>(DependencyObject root, string name) where T : FrameworkElement
		{
			for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
			{
				var child = VisualTreeHelper.GetChild(root, i);
				if (child is T match && match.Name == name)
				{
					return match;
				}
				if (FindNamed<T>(child, name) is { } found)
				{
					return found;
				}
			}
			return null;
		}

		// Enter or a row click. The box closes its list on submit, so when the online fallback comes back with
		// several places to pick from, reopen it. A place flown to lands in the box through the one-way
		// QueryText binding (the same path a PastCast saved event's town pin takes).
		private async void OnPlaceSearchQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
		{
			if (ViewModel is not { } vm)
			{
				return;
			}
			var went = await vm.PlaceSearch.SubmitAsync(args.QueryText, args.ChosenSuggestion as PlaceResult);
			if (went is null && vm.PlaceSearch.Suggestions.Count > 0)
			{
				sender.IsSuggestionListOpen = true;
			}
		}
	}
}
