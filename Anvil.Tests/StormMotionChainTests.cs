using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Anvil.Models;
using Anvil.Services;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// The provider chain (doc 01 §5) and the pieces of <see cref="Level3NvwProvider"/> that need no network.
	/// HTTP is deliberately not exercised here — these tests are offline and deterministic, like the rest of
	/// the project.
	/// </summary>
	public class StormMotionChainTests
	{
		private sealed class StubProvider : IWindProfileProvider
		{
			private readonly WindProfile? _profile;
			private readonly bool _throws;

			public StubProvider(string name, WindProfile? profile, bool throws = false)
			{
				Name = name;
				_profile = profile;
				_throws = throws;
			}

			public string Name { get; }

			public int Calls { get; private set; }

			public Task<WindProfile?> TryGetAsync(string siteId, DateTime when, CancellationToken ct = default)
			{
				Calls++;
				return _throws
					? throw new InvalidOperationException("provider blew up")
					: Task.FromResult(_profile);
			}
		}

		/// <summary>A profile that satisfies Bunkers coverage, with a curved hodograph.</summary>
		private static WindProfile Good(string source)
		{
			var levels = new List<WindProfileLevel>();
			for (var z = 0.0; z <= 6000.0; z += 250.0)
			{
				var t = Math.PI / 2 * (z / 6000.0);
				levels.Add(new WindProfileLevel(z, (20 * Math.Sin(t)) + 2, (-20 * Math.Cos(t)) + 12, 0, 0, 0));
			}

			return new WindProfile(levels, DateTime.UnixEpoch, source, "TEST", 0);
		}

		/// <summary>A profile that parses fine but is too shallow for Bunkers.</summary>
		private static WindProfile TooShallow(string source)
		{
			var levels = new List<WindProfileLevel>();
			for (var z = 0.0; z <= 4250.0; z += 250.0)
			{
				levels.Add(new WindProfileLevel(z, 10, 1, 0, 0, 0));
			}

			return new WindProfile(levels, DateTime.UnixEpoch, source, "TEST", 0);
		}

		[Fact]
		public async Task FirstProviderWithAUsableProfileWins()
		{
			var a = new StubProvider("NVW", Good("NVW"));
			var b = new StubProvider("VAD-L2", Good("VAD-L2"));
			var svc = new StormMotionService(new IWindProfileProvider[] { a, b });

			var r = await svc.ResolveAsync("KTLX", DateTime.UnixEpoch);
			Assert.True(r.HasSolution);
			Assert.Equal("NVW", r.ProfileSource);
			Assert.Equal(1, a.Calls);
			Assert.Equal(0, b.Calls); // the chain must STOP, not query every source
		}

		[Fact]
		public async Task FallsThroughWhenAProviderHasNoData()
		{
			var a = new StubProvider("NVW", null);
			var b = new StubProvider("VAD-L2", Good("VAD-L2"));
			var svc = new StormMotionService(new IWindProfileProvider[] { a, b });

			var r = await svc.ResolveAsync("KTLX", DateTime.UnixEpoch);
			Assert.True(r.HasSolution);
			Assert.Equal("VAD-L2", r.ProfileSource);
		}

		[Fact]
		public async Task FallsThroughWhenAProfileFailsCoverage()
		{
			// The point of the chain: a source can ANSWER and still be unusable.
			var a = new StubProvider("NVW", TooShallow("NVW"));
			var b = new StubProvider("VAD-L2", Good("VAD-L2"));
			var svc = new StormMotionService(new IWindProfileProvider[] { a, b });

			var r = await svc.ResolveAsync("KTLX", DateTime.UnixEpoch);
			Assert.True(r.HasSolution);
			Assert.Equal("VAD-L2", r.ProfileSource);
		}

		[Fact]
		public async Task AThrowingProviderDoesNotBreakTheChain()
		{
			var a = new StubProvider("NVW", null, throws: true);
			var b = new StubProvider("VAD-L2", Good("VAD-L2"));
			var svc = new StormMotionService(new IWindProfileProvider[] { a, b });

			var r = await svc.ResolveAsync("KTLX", DateTime.UnixEpoch);
			Assert.True(r.HasSolution);
			Assert.Equal("VAD-L2", r.ProfileSource);
		}

		[Fact]
		public async Task ReportsTheInformativeFailureNotTheLastOne()
		{
			// NVW answered with a real but too-shallow profile; VAD-L2 had nothing. The useful reason to show
			// the user is "InsufficientDepth", not the trailing provider's "NoProfile".
			var a = new StubProvider("NVW", TooShallow("NVW"));
			var b = new StubProvider("VAD-L2", null);
			var svc = new StormMotionService(new IWindProfileProvider[] { a, b });

			var r = await svc.ResolveAsync("KTLX", DateTime.UnixEpoch);
			Assert.False(r.HasSolution);
			Assert.Equal(StormMotionFailure.InsufficientDepth, r.Failure);
			Assert.Equal("NVW", r.ProfileSource);
		}

		[Fact]
		public async Task NoProvidersYieldsNoProfile()
		{
			var svc = new StormMotionService(Array.Empty<IWindProfileProvider>());
			var r = await svc.ResolveAsync("KTLX", DateTime.UnixEpoch);
			Assert.Equal(StormMotionFailure.NoProfile, r.Failure);
		}

		[Theory]
		[InlineData("KTLX", "TLX")]
		[InlineData("KBGM", "BGM")]
		[InlineData("TDFW", "DFW")]   // TDWR ids do not start with K
		[InlineData("PGUA", "GUA")]   // nor do the OCONUS sites
		[InlineData("ktlx", "TLX")]
		[InlineData("", null)]
		[InlineData("XY", null)]
		public void SiteIdIsReducedToTheBucketsThreeLetterForm(string input, string? expected)
			=> Assert.Equal(expected, Level3NvwProvider.ToThreeLetterSite(input));

		[Fact]
		public void KeyTimestampsParse()
		{
			Assert.True(Level3NvwProvider.TryParseKeyTime("TLX_NVW_2020_03_31_00_02_54", out var when));
			Assert.Equal(new DateTime(2020, 3, 31, 0, 2, 54, DateTimeKind.Utc), when);

			Assert.False(Level3NvwProvider.TryParseKeyTime("TLX_NVW_2020_03_31", out _));
			Assert.False(Level3NvwProvider.TryParseKeyTime("garbage", out _));
		}
	}
}
