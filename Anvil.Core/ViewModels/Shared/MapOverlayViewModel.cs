using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>
	/// Base for a simple cached-GeoJSON map overlay VM (SPC watches, storm-based warnings, …): a
	/// show/hide toggle, an overall opacity, the map-ready latch, and the "re-point the page at the
	/// freshly-cached file" push — the boilerplate that <see cref="WatchesViewModel"/> and
	/// <see cref="WarningsViewModel"/> used to hand-roll identically.
	///
	/// A subclass supplies the three <see cref="Anvil.Services.IMapService"/> pushes (visible / opacity /
	/// source) and the cache URL, and still owns its OWN data refresh loop — the cadence differs per feed
	/// (watches poll on a fixed interval, warnings adaptively) — calling <see cref="RepushSource"/> after a
	/// cycle that pulled fresh data.
	/// </summary>
	public abstract class MapOverlayViewModel : ObservableObject
	{
		/// <summary>Readiness guard: overlay commands only run once the map page has reported 'mapReady'
		/// (set by <see cref="OnMapsReadyAsync"/>, called from <c>MapViewModel.OnMapsReadyAsync</c>).</summary>
		protected bool IsMapReady { get; private set; }

		// Overlay toggle. Default OFF so the app launches with nothing drawn.
		private bool _isVisible;

		// Overall opacity of the overlay (fill + outline together). Default 1.0 = the current look.
		private double _opacity = 1.0;

		/// <summary>Show/hide the overlay on the map (default off). Pushes to the page once ready.</summary>
		public bool IsVisible
		{
			get => _isVisible;
			set
			{
				if (SetProperty(ref _isVisible, value) && IsMapReady)
				{
					_ = SetVisibleAsync(value);
				}
			}
		}

		/// <summary>Overall opacity (0-1) of the overlay — scales the faint fill and the bold outline
		/// together (1 = the default look). Independent of the show/hide toggle.</summary>
		public double Opacity
		{
			get => _opacity;
			set
			{
				if (SetProperty(ref _opacity, value) && IsMapReady)
				{
					_ = SetOpacityAsync(value);
				}
			}
		}

		/// <summary>The cache URL the page fetches this overlay's GeoJSON from (served under the service's
		/// virtual host).</summary>
		protected abstract string SourceUrl { get; }

		/// <summary>Push the show/hide state to the page (the feature-specific IMapService call).</summary>
		protected abstract Task SetVisibleAsync(bool visible);

		/// <summary>Push the opacity to the page (the feature-specific IMapService call).</summary>
		protected abstract Task SetOpacityAsync(double opacity);

		/// <summary>Point the page at the cache URL so it (re)fetches the GeoJSON (the feature-specific
		/// IMapService call).</summary>
		protected abstract Task SetSourceAsync(string url);

		/// <summary>Called by MapViewModel once the map page is ready: points the page at the cached overlay
		/// and applies the current opacity + toggle state.</summary>
		public virtual async Task OnMapsReadyAsync()
		{
			IsMapReady = true;
			await SetSourceAsync(SourceUrl);
			await SetOpacityAsync(_opacity);
			await SetVisibleAsync(_isVisible);
		}

		/// <summary>Re-points the page at the (freshly-cached) file so it reloads. Called after a background
		/// refresh; the page only re-fetches when the overlay is shown. No-op until the map is ready.</summary>
		protected void RepushSource()
		{
			if (IsMapReady)
			{
				_ = SetSourceAsync(SourceUrl);
			}
		}
	}
}
