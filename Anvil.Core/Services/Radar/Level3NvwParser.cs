using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>
	/// Decoder for NEXRAD Level III product 48 (mnemonic NVW, "VAD Wind Profile") — the NWS's own VAD wind
	/// profile, computed by the ORPG from dealiased velocity. Pure: no I/O, no clock, no logging.
	/// </summary>
	/// <remarks>
	/// Format notes and their provenance live in <c>docs/radar/05-nvw-product-48.md</c>; every structural claim
	/// below was verified against a real product (<c>TLX_NVW_2020_03_31_00_02_54</c>) and is pinned by
	/// <c>Anvil.Tests/Level3NvwParserTests.cs</c>.
	///
	/// <para>⚠️ THE WINDS ARE IN THE TABULAR BLOCK, NOT THE SYMBOLOGY BLOCK. Product 48 is not a "stand-alone
	/// tabular" product (that ICD term means a product with NO symbology block — 62/75/77/82), but it does
	/// carry a Tabular Alphanumeric block as Block ID 3. Conflating those two concepts leads you to parse the
	/// wind BARBS in the symbology block instead, which carry no numeric altitude and only a 5-level colour
	/// tier in place of RMS. The tabular block has explicit ALT and RMS columns, which is what QC needs.</para>
	///
	/// <para>⚠️ ALT IS MSL, IN HUNDREDS OF FEET. Bunkers layers are AGL, so the radar height (PDB halfword 15,
	/// feet MSL) must be subtracted. At a 5,000 ft site, skipping that shifts the whole profile up ~1.5 km and
	/// corrupts both the mean wind and the shear. Verified empirically: for 26 of 27 levels in the reference
	/// product, ALT×100 − (beam height from SRNG/ELEV) equals the radar height to within a few hundred feet.</para>
	///
	/// <para>⚠️ A ROW'S SRNG/ELEV CAN BE INTERNALLY INCONSISTENT. The reference product has one level whose
	/// stated slant range and elevation imply 33,000 ft against a reported 15,200 ft. Never derive a level's
	/// height from SRNG/ELEV — read ALT. They are kept only for diagnostics.</para>
	/// </remarks>
	public static class Level3NvwParser
	{
		private const int VadProductCode = 48;
		private const int TabularBlockId = 3;
		private const double FeetPerMetre = 3.280839895013123;

		/// <summary>Message header (18 bytes) + product description block (102 bytes).</summary>
		private const int MessageHeaderBytes = 18;
		private const int ProductDescriptionBytes = 102;

		/// <summary>Parses a raw NVW product file. Returns null when the bytes are not a usable product —
		/// never throws for malformed input, since "no profile" is an ordinary outcome (clear air aloft
		/// legitimately yields a product whose table has no rows).</summary>
		/// <param name="file">The complete product file, exactly as fetched.</param>
		/// <param name="siteId">Site id to stamp on the result (the product carries only the office).</param>
		public static WindProfile? Parse(byte[] file, string siteId = "")
		{
			if (file is null || file.Length < 64)
			{
				return null;
			}

			try
			{
				var msg = Unwrap(file);
				if (msg.Length < MessageHeaderBytes + ProductDescriptionBytes)
				{
					return null;
				}

				var span = msg.AsSpan();
				if (ReadI16(span, 0) != VadProductCode)
				{
					return null;
				}

				// Message header: code, date (modified Julian), time (s after 00Z), length, ids, block count.
				var msgDate = ReadI16(span, 2);
				var msgTime = ReadI32(span, 4);

				// Integrity check on the framing. msg_len counts from the message start, so a mis-assembled
				// zlib frame set (or a bad text-header skip) shows up here rather than as garbage offsets
				// later. On the reference product this is an exact equality.
				var msgLen = ReadI32(span, 8);
				if (msgLen < MessageHeaderBytes + ProductDescriptionBytes || msgLen > span.Length)
				{
					return null;
				}

				// Product description block. Halfword n starts at byte 2(n-1), counting from the message start.
				var radarHeightFt = ReadI16(span, Hw(15));
				var tabOff = ReadU32(span, Hw(59));
				if (tabOff == 0)
				{
					return null; // no tabular block -> nothing we can use (symbology fallback is not implemented)
				}

				var tabStart = checked((int)(2 * tabOff));
				if (tabStart + 8 > span.Length || ReadI16(span, tabStart + 2) != TabularBlockId)
				{
					return null;
				}

				var levels = ParseVadPages(span, tabStart, radarHeightFt);
				return new WindProfile(levels, ToUtc(msgDate, msgTime), "NVW", siteId, radarHeightFt);
			}
			catch (Exception)
			{
				return null; // malformed product: the caller falls through to the next wind-profile source
			}
		}

		/// <summary>Byte offset of ICD halfword <paramref name="n"/> from the message start.</summary>
		private static int Hw(int n) => 2 * (n - 1);

		/// <summary>
		/// Strips transport framing and returns the Level III message. Framing is SOURCE-dependent, so this
		/// is deliberately tolerant: a text (WMO/AWIPS) header may precede the body, the body may be zlib
		/// (in CONCATENATED FRAMES — the reference product has three), and a second text header may sit
		/// inside the inflated data.
		/// <para>⚠️ The zlib here is LDM/NOAAPORT transport framing, NOT the ICD's own product compression
		/// (PDB halfword 51), which is bzip2 and is set to 0 for a product this small. Different layers.</para>
		/// </summary>
		private static byte[] Unwrap(byte[] file)
		{
			var body = Inflate(file);
			var start = SkipTextHeader(body);
			return start == 0 ? body : body[start..];
		}

		/// <summary>
		/// Inflates every concatenated zlib frame. .NET has no `unused_data` equivalent, so frames are located
		/// by scanning for valid zlib headers (CM=8 and the CMF/FLG checksum) and inflating from each; a frame
		/// stops on its own and any trailing bytes are ignored, so frame ENDS never need to be computed.
		/// A false-positive header would corrupt the output, which is why the caller validates the product
		/// code afterwards. Returns the input unchanged when nothing inflates (an uncompressed product).
		/// </summary>
		private static byte[] Inflate(byte[] file)
		{
			var output = new MemoryStream();
			var found = false;
			for (var i = 0; i + 1 < file.Length; i++)
			{
				if (!IsZlibHeader(file, i))
				{
					continue;
				}

				// ⚠️ Inflate into a SEPARATE buffer and commit only on success. A candidate offset that is NOT
				// a real frame boundary can emit bytes BEFORE it fails, and appending those corrupts the
				// message. (Python's decompressobj raises without yielding partial output, so a mirror of this
				// written in Python will NOT reproduce the bug — it cost a debugging round.)
				var frame = new MemoryStream();
				try
				{
					using var input = new MemoryStream(file, i, file.Length - i, writable: false);
					using var zlib = new ZLibStream(input, CompressionMode.Decompress);
					zlib.CopyTo(frame);
				}
				catch (Exception)
				{
					continue; // a byte pair inside compressed data that merely looked like a header
				}

				if (frame.Length == 0)
				{
					continue;
				}

				frame.Position = 0;
				frame.CopyTo(output);
				found = true;
			}

			return found ? output.ToArray() : file;
		}

		/// <summary>A zlib header is CM=8 in the low nibble of CMF, with (CMF&lt;&lt;8 | FLG) a multiple of 31.</summary>
		private static bool IsZlibHeader(byte[] b, int i)
			=> (b[i] & 0x0F) == 8 && ((b[i] << 8) | b[i + 1]) % 31 == 0;

		/// <summary>
		/// Length of a leading WMO/AWIPS text header, or 0 if absent. The header is a short run of printable
		/// ASCII and CR/LF ending in a CR CR LF pair; the message that follows starts with the big-endian
		/// product code, so a text header is unambiguous.
		/// </summary>
		private static int SkipTextHeader(byte[] body)
		{
			var limit = Math.Min(80, body.Length);
			var last = -1;
			for (var i = 0; i + 2 < limit; i++)
			{
				if (body[i] == '\r' && body[i + 1] == '\r' && body[i + 2] == '\n')
				{
					last = i + 3;
					i += 2;
				}
			}

			return last < 0 ? 0 : last;
		}

		/// <summary>
		/// Reads the tabular block's pages and returns the levels from the "VAD Algorithm Output" page.
		/// <para>⚠️ Pages are selected BY HEADER TEXT, never by index: the ICD requires RPG site adaptable
		/// parameters to be appended as the last page(s), so a product carries a mix of page kinds. The
		/// reference product has two VAD pages, one adaptable-parameters page and one blank.</para>
		/// </summary>
		private static List<WindProfileLevel> ParseVadPages(ReadOnlySpan<byte> msg, int tabStart, int radarHeightFt)
		{
			// Block header (8) + a SECOND message header (18) + a SECOND product description block (102).
			var p = tabStart + 8 + MessageHeaderBytes + ProductDescriptionBytes;
			p += 2; // block divider
			var pages = ReadI16(msg, p);
			p += 2;

			var levels = new List<WindProfileLevel>();
			var line = new StringBuilder();
			var inVadPage = false;
			var pageStarted = false;

			for (var pageIndex = 0; pageIndex < pages && p + 2 <= msg.Length; )
			{
				var chars = ReadI16(msg, p);
				p += 2;
				if (chars == -1)
				{
					pageIndex++;
					inVadPage = false;
					pageStarted = false;
					continue;
				}

				if (chars < 0 || p + chars > msg.Length)
				{
					break;
				}

				line.Clear();
				line.Append(Encoding.ASCII.GetString(msg.Slice(p, chars)));
				p += chars;

				var text = line.ToString();
				if (!pageStarted)
				{
					pageStarted = true;
					inVadPage = text.TrimStart().StartsWith("VAD Algorithm Output", StringComparison.Ordinal);
				}

				if (inVadPage && TryParseVadRow(text, radarHeightFt, out var level))
				{
					levels.Add(level);
				}
			}

			levels.Sort(static (a, b) => a.HeightAglM.CompareTo(b.HeightAglM));
			return levels;
		}

		/// <summary>
		/// Parses one data row of the VAD Algorithm Output table:
		/// <code>ALT  U  V  W  DIR  SPD  RMS  DIV  SRNG  ELEV
		///      100ft m/s m/s cm/s deg kts kts E-3/s nm deg</code>
		/// W and DIV read "NA" when absent, which is why this splits on whitespace rather than fixed columns.
		/// Header and units rows are rejected because ALT must be three digits.
		/// </summary>
		private static bool TryParseVadRow(string text, int radarHeightFt, out WindProfileLevel level)
		{
			level = default!;
			var f = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
			if (f.Length != 10 || f[0].Length != 3 || !int.TryParse(f[0], out var altHundredsFt))
			{
				return false;
			}

			if (!double.TryParse(f[1], out var u) || !double.TryParse(f[2], out var v)
				|| !int.TryParse(f[4], out var dir) || !int.TryParse(f[5], out var spd)
				|| !double.TryParse(f[6], out var rms))
			{
				return false;
			}

			// MSL -> above radar level, then feet -> metres. See the class remarks.
			var heightAglM = (altHundredsFt * 100.0 - radarHeightFt) / FeetPerMetre;
			level = new WindProfileLevel(heightAglM, u, v, spd, dir, rms);
			return true;
		}

		/// <summary>Modified Julian date (day 1 = 1 Jan 1970) + seconds after 00Z. ⚠️ The epoch is 31 Dec 1969,
		/// not 1 Jan 1970 — an off-by-one here is silent.</summary>
		private static DateTime ToUtc(int modifiedJulianDay, int secondsAfterMidnight)
			=> new DateTime(1969, 12, 31, 0, 0, 0, DateTimeKind.Utc)
				.AddDays(modifiedJulianDay)
				.AddSeconds(secondsAfterMidnight);

		private static short ReadI16(ReadOnlySpan<byte> b, int off) => BinaryPrimitives.ReadInt16BigEndian(b[off..]);

		private static int ReadI32(ReadOnlySpan<byte> b, int off) => BinaryPrimitives.ReadInt32BigEndian(b[off..]);

		private static uint ReadU32(ReadOnlySpan<byte> b, int off) => BinaryPrimitives.ReadUInt32BigEndian(b[off..]);
	}
}
