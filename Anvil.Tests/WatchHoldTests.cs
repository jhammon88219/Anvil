using System;
using Anvil.Services;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>The WWA watch feed flaps to a spurious empty; SpcWatchService holds the last-known-good set
	/// through it, pruning only watches whose own expiration has passed. These pin that pruning.</summary>
	public class WatchHoldTests
	{
		private const string Cached = """
			{"type":"FeatureCollection","features":[
			 {"type":"Feature","geometry":null,"properties":{"phenom":"TO","expiration":"2026-10-09T21:00:00-04:00"}},
			 {"type":"Feature","geometry":null,"properties":{"phenom":"SV","expiration":"2026-10-09T23:00:00-04:00"}},
			 {"type":"Feature","geometry":null,"properties":{"phenom":"SV","expiration":1791594000000}},
			 {"type":"Feature","geometry":null,"properties":{"phenom":"FA"}}
			]}
			""";

		private static (int Tornado, int Severe, int Flood) Counts(string json)
		{
			Assert.True(SpcWatchService.TryGetFeatureCounts(json, out _, out _, out var t, out var s, out var f));
			return (t, s, f);
		}

		[Fact]
		public void Before_any_expiry_everything_is_held()
		{
			var held = SpcWatchService.PruneExpired(Cached, DateTimeOffset.Parse("2026-10-09T22:00:00Z"));
			Assert.Equal((1, 2, 1), Counts(held!));
		}

		[Fact]
		public void An_expired_watch_drops_out_of_the_held_set()
		{
			// 01:30Z = 21:30 EDT: the TO (21:00 EDT) and the epoch-ms SV (2026-10-10T01:00Z) are past.
			var held = SpcWatchService.PruneExpired(Cached, DateTimeOffset.Parse("2026-10-10T01:30:00Z"));
			Assert.Equal((0, 1, 1), Counts(held!));
		}

		[Fact]
		public void Unparseable_cache_returns_null()
		{
			Assert.Null(SpcWatchService.PruneExpired("{\"error\":{}}", DateTimeOffset.UtcNow));
		}
	}
}
