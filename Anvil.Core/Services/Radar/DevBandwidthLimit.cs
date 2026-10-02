using System;
using System.Threading;
using System.Threading.Tasks;

namespace Anvil.Services
{
	/// <summary>
	/// DEV-ONLY simulated slow connection for the radar fetch path (Settings → Dev → Load-time seeding): caps the
	/// TOTAL bytes/second of every Level II download in the process, so load-time data can be collected at speeds other
	/// than the developer's own link. 0 = off (the default; a Release build never sets it).
	/// </summary>
	/// <remarks>
	/// ⚠️ A SHARED budget, like a real link: concurrent downloads queue behind each other. Charged AFTER a body arrives
	/// (the bytes land at full speed, then the caller waits out their share), so per-request timing is lumpy but the
	/// throughput over a load is the cap — which is what the load-time log measures.
	/// </remarks>
	public static class DevBandwidthLimit
	{
		private static readonly object Gate = new();
		private static double _bytesPerSecond;
		private static DateTimeOffset _freeAt = DateTimeOffset.MinValue;

		/// <summary>The cap in megabits/second; 0 = no cap.</summary>
		public static double Mbps
		{
			get { lock (Gate) return _bytesPerSecond * 8 / 1_000_000; }
			set { lock (Gate) { _bytesPerSecond = Math.Max(0, value) * 1_000_000 / 8; _freeAt = DateTimeOffset.MinValue; } }
		}

		/// <summary>Wait out <paramref name="bytes"/>' share of the cap (returns at once when off).</summary>
		public static async Task ChargeAsync(long bytes, CancellationToken ct)
		{
			TimeSpan wait;
			lock (Gate)
			{
				if (_bytesPerSecond <= 0 || bytes <= 0) return;
				var now = DateTimeOffset.UtcNow;
				var start = _freeAt > now ? _freeAt : now;
				_freeAt = start + TimeSpan.FromSeconds(bytes / _bytesPerSecond);
				wait = _freeAt - now;
			}
			if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
		}
	}
}
