using System;
using System.Collections.Generic;

namespace Anvil.Models
{
	/// <summary>
	/// One storm cell in one volume scan, as the WSR-88D's own storm algorithms describe it (SCIT cell
	/// identification + tracking, TDA, MDA, HDA — the Level III NST/NSS/NTV/NMD/NHI products), read from
	/// IEM's parsed storm-attribute table. Units are the products' own: heights kft MSL, hail inches, motion
	/// knots.
	/// </summary>
	/// <param name="Id">The SCIT cell id ("H1"). ⚠️ Recycled once a cell dies, so an id only means one storm
	/// over a contiguous run of scans — see <c>StormCellTracks</c>.</param>
	/// <param name="Tvs">"TVS", "ETVS" (elevated), or "" for none.</param>
	/// <param name="MesoRank">The MDA strength rank (1–25), 0 for none. NWS treats 5+ as a mesocyclone.</param>
	/// <param name="Posh">Probability of SEVERE hail (≥ 1 in), percent.</param>
	/// <param name="Poh">Probability of any hail, percent.</param>
	/// <param name="MaxHailIn">Maximum expected hail size, inches.</param>
	/// <param name="Vil">Cell-based vertically integrated liquid, kg/m².</param>
	/// <param name="DirFromDeg">Forecast motion, the direction it moves FROM (meteorological). With
	/// <paramref name="SpeedKt"/> = 0 the cell is new and has no motion yet.</param>
	public sealed record StormCell(
		string Id, double Lat, double Lon,
		string Tvs, int MesoRank, int Posh, int Poh, double MaxHailIn,
		int Vil, int MaxDbz, double MaxDbzHeightKft, double TopKft,
		int DirFromDeg, int SpeedKt);

	/// <summary>Every cell in one volume scan. <see cref="Valid"/> is the volume's start time.</summary>
	public sealed record StormCellScan(DateTimeOffset Valid, IReadOnlyList<StormCell> Cells);

	/// <summary>The thresholds the map and the card share, so "a mesocyclone" means one thing.</summary>
	public static class StormCellThresholds
	{
		/// <summary>MDA strength rank at which a circulation is a mesocyclone (NWS; below it is weak shear).</summary>
		public const int MesoRank = 5;

		/// <summary>POSH at which a cell is drawn as a hail cell — severe hail more likely than not.</summary>
		public const int SeverePosh = 50;

		/// <summary>How far back a cell's past track reaches.</summary>
		public static readonly TimeSpan PastTrack = TimeSpan.FromMinutes(30);

		/// <summary>
		/// The longest a frame may sit after its scan and still show it. ⚠️ Live, IEM runs a few minutes behind
		/// the chunks-bucket frame, so the newest frame usually has NO scan of its own — this is what lets it
		/// show the latest one. Past it, cells would describe a different storm state than the radar.
		/// </summary>
		public static readonly TimeSpan MaxFrameLag = TimeSpan.FromMinutes(12);

		public static bool IsMeso(StormCell c) => c.MesoRank >= MesoRank;

		public static bool IsHail(StormCell c) => c.Posh >= SeverePosh;

		public static bool IsTvs(StormCell c) => c.Tvs.Length > 0;
	}
}
