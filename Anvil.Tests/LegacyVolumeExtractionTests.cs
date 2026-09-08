using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Anvil.Services;
using Xunit;
using Xunit.Abstractions;

namespace Anvil.Tests
{
	/// <summary>
	/// Covers <c>Level2Format.TryExtractLowestTiltUncompressed</c> — the walker for LEGACY (.gz-sourced)
	/// volumes, which gunzip to a fully uncompressed AR2V with no bzip2 LDM records.
	/// </summary>
	/// <remarks>
	/// ⚠️ <b>Two halves, and they answer different questions.</b> The synthetic tests below are
	/// deterministic and always run: they pin the decisions (split-cut pairing, the settling-radial trap,
	/// bail-outs). But a synthetic volume can only ever confirm OUR OWN reading of the format — the same
	/// reason <c>Fixtures/</c> holds a real Level III product. So <see cref="RealCachedLegacyVolumes"/>
	/// runs the extractor over whatever real legacy volumes happen to be in the app's cache, and SKIPS
	/// when there are none. It is the half that could actually catch a misread of the spec.
	/// <para>⚠️ The real-data half is deliberately NOT a hard requirement — a clean machine has an empty
	/// cache, and a test that fails for lack of a 43 MB file nobody checked in would just get muted. It
	/// reports what it found through <see cref="ITestOutputHelper"/> so a run says whether it did anything.</para>
	/// </remarks>
	public class LegacyVolumeExtractionTests
	{
		private readonly ITestOutputHelper _out;

		public LegacyVolumeExtractionTests(ITestOutputHelper output) => _out = output;

		// ── Synthetic half: deterministic, always runs ────────────────────────────────────────────────

		/// <summary>
		/// Builds an UNCOMPRESSED AR2V: 24-byte header, then messages of
		/// [12-byte CTM][16-byte message header][body]. Non-31 types sit in a fixed 2432-byte frame;
		/// Message 31 is variable and declares its own size in halfwords at messageHeader+0.
		/// </summary>
		private static byte[] BuildVolume(IEnumerable<(int Type, int Elev, float Angle, int BodyLen)> messages)
		{
			using var ms = new MemoryStream();
			ms.Write(System.Text.Encoding.ASCII.GetBytes("AR2V0006.663"));      // 12
			ms.Write(new byte[12]);                                             // -> 24-byte header

			foreach (var (type, elev, angle, bodyLen) in messages)
			{
				var body = new byte[Math.Max(bodyLen, 28)];
				System.Text.Encoding.ASCII.GetBytes("KTLX").CopyTo(body, 0);    // ICAO at body+0
				body[22] = (byte)elev;                                          // elevation NUMBER
				System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(body.AsSpan(24, 4), angle);

				var rec = type == 31 ? 12 + 16 + body.Length : 2432;
				var buf = new byte[rec];
				var sizeHalfwords = (16 + body.Length) / 2;                     // header + body, in halfwords
				buf[12] = (byte)(sizeHalfwords >> 8);
				buf[13] = (byte)(sizeHalfwords & 0xFF);
				buf[15] = (byte)type;                                           // messageHeader+3
				if (type == 31)
				{
					body.CopyTo(buf, 28);
				}
				ms.Write(buf);
			}
			return ms.ToArray();
		}

		private static (int Type, int Elev, float Angle, int BodyLen) Meta() => (5, 0, 0f, 0);
		private static (int Type, int Elev, float Angle, int BodyLen) Radial(int elev, float angle, int len = 200)
			=> (31, elev, angle, len);

		[Fact]
		public void KeepsBaseCutAndItsDopplerCompanion_AndStopsAtTheNextTilt()
		{
			// A split cut: surveillance (elev 1) + its Doppler companion (elev 2) at the SAME 0.5°, then a
			// genuinely higher tilt that must not be kept.
			var vol = BuildVolume(new[]
			{
				Meta(), Meta(),
				Radial(1, 0.26f), Radial(1, 0.48f), Radial(1, 0.50f),   // base, first radial settles low
				Radial(2, 0.48f), Radial(2, 0.50f),                     // Doppler companion, same angle
				Radial(3, 1.31f), Radial(3, 1.33f),                     // next tilt — must be dropped
			});

			var outp = Level2Format.TryExtractLowestTiltUncompressed(vol, "KTLX", out var completed);

			Assert.NotNull(outp);
			Assert.True(completed);
			Assert.True(outp!.Length < vol.Length);
			Assert.Equal(3, CountRadialsAtElev(outp!, 1));      // all three base radials kept
			Assert.Equal(2, CountRadialsAtElev(outp!, 2));      // its Doppler companion kept
			Assert.Equal(0, CountRadialsAtElev(outp!, 3));      // the genuinely higher tilt dropped
		}

		/// <summary>
		/// ⚠️ THE REGRESSION THIS FILE EXISTS FOR. The first radial of a cut is a SETTLING radial reading
		/// low — a real 0.5° base reads 0.26°. If the cut's angle is anchored on that instead of tracked as
		/// the max over its radials, the 0.48° companion sits 0.22° away, outside the 0.20° tolerance, and
		/// is dropped — leaving a tilt with NO VELOCITY. Mutate the `angle > baseAngle` tracking in
		/// TryExtractLowestTiltUncompressed and this test must go red.
		/// </summary>
		[Fact]
		public void SettlingFirstRadialDoesNotCostTheDopplerCompanion()
		{
			var vol = BuildVolume(new[]
			{
				Meta(),
				Radial(1, 0.26f),                 // settling radial, reads 0.22° below the true angle
				Radial(1, 0.48f),
				Radial(2, 0.48f), Radial(2, 0.49f),
			});

			var outp = Level2Format.TryExtractLowestTiltUncompressed(vol, "KTLX", out _);

			Assert.NotNull(outp);
			Assert.True(CountRadialsAtElev(outp!, 2) > 0,
				"the Doppler companion was dropped — the extract would have no velocity");
		}

		[Fact]
		public void RejectsAVolumeWhoseBaseCutHasNoDopplerCompanion()
		{
			// A base cut alone is worse than no extraction: the caller would cache it and every frame of
			// the replay would silently lose Velocity/SRV. Falling back to the whole volume is correct.
			var vol = BuildVolume(new[] { Meta(), Radial(1, 0.48f), Radial(1, 0.50f), Radial(2, 1.45f) });

			Assert.Null(Level2Format.TryExtractLowestTiltUncompressed(vol, "KTLX", out _));
		}

		[Fact]
		public void RejectsNonAr2vAndMessage1OnlyVolumes()
		{
			var notAr2v = new byte[4096];
			Assert.Null(Level2Format.TryExtractLowestTiltUncompressed(notAr2v, "KTLX", out _));

			// Pre-2008 volumes carry Message 1, not 31 — no radials are recognised, so nothing is kept and
			// the caller falls back to the whole volume rather than caching a bogus extract.
			var msg1Only = BuildVolume(new[] { Meta(), (1, 1, 0.5f, 0), (1, 2, 0.5f, 0) });
			Assert.Null(Level2Format.TryExtractLowestTiltUncompressed(msg1Only, "KTLX", out _));
		}

		[Fact]
		public void BailsOnADesyncedWalkRatherThanReturningGarbage()
		{
			var vol = BuildVolume(new[] { Meta(), Radial(1, 0.48f), Radial(2, 0.48f) });
			// Corrupt the first radial's declared size so the walk lands mid-record. The callsign alignment
			// check must catch it; the result must never be a plausible-looking buffer.
			vol[24 + 2432 + 12] = 0x7F;
			vol[24 + 2432 + 13] = 0xFF;

			var outp = Level2Format.TryExtractLowestTiltUncompressed(vol, "KTLX", out _);
			Assert.Null(outp);
		}

		// Counts Message 31 records at a given elevation number by re-walking the output the same way.
		private static int CountRadialsAtElev(byte[] data, int elev)
		{
			var n = 0;
			var pos = 24;
			while (pos + 28 <= data.Length)
			{
				var mh = pos + 12;
				var sizeHalfwords = (data[mh] << 8) | data[mh + 1];
				var type = data[mh + 3];
				var rec = type == 31 ? 12 + sizeHalfwords * 2 : 2432;
				if (rec <= 28 || pos + rec > data.Length)
				{
					break;
				}
				if (type == 31 && data[mh + 16 + 22] == elev)
				{
					n++;
				}
				pos += rec;
			}
			return n;
		}

		// ── Real-data half: runs only when the app's cache holds legacy volumes ───────────────────────

		[Fact]
		public void RealCachedLegacyVolumes()
		{
			var dir = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				"Packages", "Anvil_c78sma71wz9qr", "LocalCache", "Local", "Anvil", "RadarLevel2");
			if (!Directory.Exists(dir))
			{
				dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
					"Anvil", "RadarLevel2");
			}
			if (!Directory.Exists(dir))
			{
				_out.WriteLine("no radar cache on this machine — skipped");
				return;
			}

			// A legacy volume is one the LDM extractor could not touch, so it was cached whole: >20 MB.
			// An already-extracted modern tilt is ~7-10 MB and is not what this exercises.
			var legacy = Directory.EnumerateFiles(dir, "*.V06")
				.Where(f => !f.Contains("_live_", StringComparison.OrdinalIgnoreCase))
				.Where(f => new FileInfo(f).Length > 20L * 1024 * 1024)
				.Take(12)
				.ToList();
			if (legacy.Count == 0)
			{
				_out.WriteLine("no legacy (>20 MB) volumes cached — skipped; load a pre-2016 PastCast event to populate");
				return;
			}

			long srcTotal = 0, outTotal = 0;
			foreach (var path in legacy)
			{
				var raw = File.ReadAllBytes(path);
				var name = Path.GetFileName(path);

				// The LDM walker must decline it — that is WHY this path exists. If this ever fails, the
				// file is not what this test thinks it is.
				Assert.Null(Level2Format.TryExtractLowestTilt(raw, name[..4], out _));

				var outp = Level2Format.TryExtractLowestTiltUncompressed(raw, name[..4], out _);
				Assert.NotNull(outp);

				// Both moments must survive, or the replay loses Velocity/SRV.
				Assert.True(Level2Format.HasMoment(outp!, Level2Format.Dref), $"{name}: no DREF in the extract");
				Assert.True(Level2Format.HasMoment(outp!, Level2Format.Dvel), $"{name}: no DVEL in the extract");

				// The VCP table rides in the leading metadata, and the tilt combo reads it back out of the
				// extracted buffer — so dropping it would silently empty the tilt list.
				var tilts = Level2Format.ReadElevationAnglesFromExtractedTilt(outp!);
				Assert.True(tilts is { Count: > 0 }, $"{name}: no elevation table survived the extract");

				var pct = 100.0 * outp!.Length / raw.Length;
				_out.WriteLine($"{name}: {raw.Length / 1048576.0:F1} MB -> {outp.Length / 1048576.0:F2} MB " +
					$"({pct:F1}%, {(double)raw.Length / outp.Length:F1}x), {tilts!.Count} tilt(s) in table");

				// Measured over 105 cached volumes: a consistent 17.9-18.6%. A result far outside that band
				// means the pairing or the tilt boundary moved, which is exactly what this should catch.
				Assert.InRange(pct, 10.0, 30.0);

				srcTotal += raw.Length;
				outTotal += outp.Length;
			}

			_out.WriteLine($"TOTAL {legacy.Count} volume(s): {srcTotal / 1048576.0:F0} MB -> " +
				$"{outTotal / 1048576.0:F0} MB ({(double)srcTotal / outTotal:F1}x)");
		}
	}
}
