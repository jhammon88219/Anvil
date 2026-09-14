using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Anvil.Models;
using Anvil.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>
	/// The map-tools tier's place search: gazetteer suggestions per keystroke, the online geocoder on Enter
	/// when the gazetteer has nothing, and a fly-to + pin for the place found. The pin itself is a
	/// <see cref="MarkersViewModel"/> marker (kind <see cref="MarkerKind.SearchResult"/>).
	/// </summary>
	/// <remarks>
	/// ⚠️ The ONLINE path is reachable only from <see cref="SubmitAsync"/>. Wiring it to text changes would
	/// breach Nominatim's no-autocomplete policy — see <see cref="IPlaceSearchService.SearchOnlineAsync"/>.
	/// ⚠️ Clearing the box is how the pin comes off: an empty query removes it. There is no other remove
	/// control while the Selected Marker window is still pending re-add.
	/// </remarks>
	public sealed class PlaceSearchViewModel : ObservableObject
	{
		private readonly IPlaceSearchService _search;
		private readonly MarkersViewModel _markers;
		private CancellationTokenSource? _onlineCts;

		public PlaceSearchViewModel(IPlaceSearchService search, MarkersViewModel markers)
		{
			_search = search;
			_markers = markers;
		}

		/// <summary>The rows under the search box. One instance for the VM's life — the view binds it once.</summary>
		public ObservableCollection<PlaceResult> Suggestions { get; } = new();

		private string _statusText = string.Empty;

		/// <summary>"Searching online…" / "No places match …" beside the box; empty when there's nothing to say.</summary>
		public string StatusText
		{
			get => _statusText;
			private set
			{
				if (SetProperty(ref _statusText, value))
				{
					OnPropertyChanged(nameof(HasStatus));
				}
			}
		}

		public bool HasStatus => _statusText.Length > 0;

		/// <summary>The user edited the text: refresh the gazetteer rows. An empty box removes the pin.</summary>
		public void UpdateSuggestions(string? text)
		{
			CancelOnline();
			StatusText = string.Empty;
			ReplaceSuggestions(_search.Suggest(text));
			if (string.IsNullOrWhiteSpace(text))
			{
				_markers.RemovePlaceMarker();
			}
		}

		/// <summary>
		/// Enter or a row click. A chosen row flies there; otherwise the best gazetteer match does; otherwise the
		/// query goes online — one online hit flies straight there, several become the rows to pick from.
		/// </summary>
		/// <returns>The place flown to, or null when nothing was (no match, or several online rows to pick).</returns>
		public async Task<PlaceResult?> SubmitAsync(string? text, PlaceResult? chosen)
		{
			if (chosen is not null)
			{
				await GoAsync(chosen);
				return chosen;
			}
			if (string.IsNullOrWhiteSpace(text))
			{
				return null;
			}

			var offline = _search.Suggest(text, 1);
			if (offline.Count > 0)
			{
				await GoAsync(offline[0]);
				return offline[0];
			}

			CancelOnline();
			var cts = _onlineCts = new CancellationTokenSource();
			StatusText = "Searching online…";
			try
			{
				var online = await _search.SearchOnlineAsync(text, cts.Token);
				if (cts.IsCancellationRequested)
				{
					return null;
				}
				switch (online.Count)
				{
					case 0:
						ReplaceSuggestions(Array.Empty<PlaceResult>());
						StatusText = $"No places match \"{text.Trim()}\"";
						return null;
					case 1:
						await GoAsync(online[0]);
						return online[0];
					default:
						ReplaceSuggestions(online);
						StatusText = "Pick a place";
						return null;
				}
			}
			catch (OperationCanceledException)
			{
				return null; // superseded by a newer edit or submit
			}
		}

		private async Task GoAsync(PlaceResult place)
		{
			CancelOnline();
			StatusText = string.Empty;
			await _markers.ShowPlaceAsync(place);
		}

		private void CancelOnline()
		{
			_onlineCts?.Cancel();
			_onlineCts = null;
		}

		private void ReplaceSuggestions(IReadOnlyList<PlaceResult> places)
		{
			Suggestions.Clear();
			foreach (var p in places)
			{
				Suggestions.Add(p);
			}
		}
	}
}
