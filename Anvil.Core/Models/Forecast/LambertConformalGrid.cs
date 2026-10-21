using System;

namespace Anvil.Models
{
	/// <summary>
	/// A GRIB2 Lambert conformal grid (template 3.30) — the HRRR's 1799 × 1059, 3 km CONUS grid — with the
	/// projection both ways: grid cell → lat/lon and lat/lon → fractional grid cell.
	/// </summary>
	/// <remarks>
	/// ⚠️ Spherical earth only (shape 6, r = 6 371 229 m, what every NCEP CAM uses); the decoder refuses an
	/// ellipsoid rather than draw it a few km off. Rows run SOUTH → NORTH (scanning mode 0x40): row 0 is the
	/// southern edge, and <c>Values[j * Nx + i]</c> is column i of row j. Longitudes come back in −180…180.
	/// Checked against the HRRR's published corners (SW 21.138 N 122.720 W, NE 47.842 N 60.917 W) by
	/// <c>Grib2DecoderTests</c>.
	/// </remarks>
	public sealed record LambertConformalGrid(
		int Nx, int Ny,
		double FirstLat, double FirstLon,   // grid point (0, 0), degrees
		double LoV,                         // the meridian the cone is cut along, degrees
		double Latin1, double Latin2,       // standard parallels, degrees
		double Dx, double Dy,               // metres
		double EarthRadius)
	{
		private readonly Cone _cone = Cone.Of(Latin1, Latin2, EarthRadius, LoV, FirstLat, FirstLon);

		public int Count => Nx * Ny;

		/// <summary>The lat/lon of grid point (i, j) — fractional points allowed.</summary>
		public (double Lat, double Lon) ToLatLon(double i, double j)
		{
			var x = _cone.X0 + i * Dx;
			var y = _cone.Y0 + j * Dy;
			var n = _cone.N;
			var rho = Math.Sign(n) * Math.Sqrt(x * x + y * y);
			var theta = n > 0 ? Math.Atan2(x, -y) : Math.Atan2(-x, y);
			var lat = 2 * Math.Atan(Math.Pow(EarthRadius * _cone.F / rho, 1 / n)) - Math.PI / 2;
			var lon = LoV + Rad2Deg(theta / n);
			return (Rad2Deg(lat), NormalizeLon(lon));
		}

		/// <summary>The fractional grid point (i, j) at a lat/lon; outside the grid when off 0…Nx−1 / 0…Ny−1.</summary>
		public (double I, double J) ToGrid(double lat, double lon)
		{
			var (x, y) = _cone.Project(lat, lon);
			return ((x - _cone.X0) / Dx, (y - _cone.Y0) / Dy);
		}

		private static double Rad2Deg(double r) => r * 180 / Math.PI;

		private static double NormalizeLon(double lon)
		{
			lon %= 360;
			if (lon > 180) lon -= 360;
			if (lon <= -180) lon += 360;
			return lon;
		}

		// The cone constants (Snyder, Map Projections, eqs. 15-1…15-5), and where grid point (0, 0) lands on it.
		private readonly record struct Cone(double N, double F, double R, double LoV, double X0, double Y0)
		{
			public static Cone Of(double latin1, double latin2, double r, double lov, double lat0, double lon0)
			{
				var p1 = Deg2Rad(latin1);
				var p2 = Deg2Rad(latin2);
				var n = Math.Abs(latin1 - latin2) < 1e-9
					? Math.Sin(p1)
					: Math.Log(Math.Cos(p1) / Math.Cos(p2)) / Math.Log(Math.Tan(Math.PI / 4 + p2 / 2) / Math.Tan(Math.PI / 4 + p1 / 2));
				var f = Math.Cos(p1) * Math.Pow(Math.Tan(Math.PI / 4 + p1 / 2), n) / n;
				var cone = new Cone(n, f, r, lov, 0, 0);
				var (x0, y0) = cone.Project(lat0, lon0);
				return cone with { X0 = x0, Y0 = y0 };
			}

			public (double X, double Y) Project(double lat, double lon)
			{
				var rho = R * F / Math.Pow(Math.Tan(Math.PI / 4 + Deg2Rad(lat) / 2), N);
				var dLon = lon - LoV;
				while (dLon > 180) dLon -= 360;
				while (dLon < -180) dLon += 360;
				var theta = N * Deg2Rad(dLon);
				return (rho * Math.Sin(theta), -rho * Math.Cos(theta));
			}

			private static double Deg2Rad(double d) => d * Math.PI / 180;
		}
	}
}
