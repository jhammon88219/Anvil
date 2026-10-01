using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>
	/// One cell of the segmented scrubber — a single loop frame. Two states: <see cref="IsDecoded"/> is
	/// the durable base (this frame's reflectivity geometry has decoded), preserved across product
	/// switches; <see cref="IsReady"/> is the DISPLAYED readiness for the ACTIVE product (decoded AND,
	/// for Velocity, dealiased) that the VM computes and the cell binds its opacity to. So the scrubber
	/// fills as frames decode for Reflectivity/CC, and re-fills as they dealias for Velocity — one
	/// consistent "loading" look for every product. <see cref="ReadyOpacity"/> is the derived double
	/// (no converter needed): full when ready, faint while still loading.
	/// </summary>
	public sealed class RadarFrameSegment : ObservableObject
	{
		private bool _isDecoded;
		private bool _isReady;

		/// <summary>Durable base state: this frame's reflectivity geometry has decoded and is renderable.
		/// Independent of the active product (the VM derives <see cref="IsReady"/> from this).</summary>
		public bool IsDecoded
		{
			get => _isDecoded;
			set => SetProperty(ref _isDecoded, value);
		}

		/// <summary>Displayed readiness for the currently-active product (set by the VM): decoded for
		/// Reflectivity/CC, and additionally dealiased for Velocity.</summary>
		public bool IsReady
		{
			get => _isReady;
			set
			{
				if (SetProperty(ref _isReady, value))
				{
					OnPropertyChanged(nameof(ReadyOpacity));
				}
			}
		}

		/// <summary>The page decoded this frame and found NOTHING to draw (or failed it) — it will never fill.
		/// Plain state, not bound: the loop holding gate counts such a frame as settled so it can't hold forever.</summary>
		public bool HasNoData { get; set; }

		/// <summary>Cell opacity: solid when the frame is ready for the active product, faint while loading.</summary>
		public double ReadyOpacity => _isReady ? 1.0 : 0.22;
	}
}
