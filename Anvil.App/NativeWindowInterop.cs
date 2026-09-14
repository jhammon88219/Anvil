using System;
using System.Runtime.InteropServices;

namespace Anvil
{
	/// <summary>
	/// The Win32 calls <see cref="WindowManager"/> needs and WinUI's AppWindow does not offer: the VISIBLE frame
	/// of a window (Windows 11 pads every resizable window with an invisible resize border, so the outer rect
	/// AppWindow reports is wider than what you see), and a window subclass that can refuse moves and resizes
	/// for a LOCKED panel. Nothing else in the app touches Win32; keep it that way.
	/// </summary>
	internal static class NativeWindowInterop
	{
		[StructLayout(LayoutKind.Sequential)]
		public struct RECT
		{
			public int Left, Top, Right, Bottom;
		}

		[StructLayout(LayoutKind.Sequential)]
		public struct POINT
		{
			public int X, Y;
		}

		[StructLayout(LayoutKind.Sequential)]
		public struct WINDOWPOS
		{
			public IntPtr Hwnd;
			public IntPtr HwndInsertAfter;
			public int X, Y, Cx, Cy;
			public uint Flags;
		}

		public delegate IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr refData);

		public const uint WM_WINDOWPOSCHANGING = 0x0046;
		public const uint WM_NCDESTROY = 0x0082;
		public const uint WM_NCHITTEST = 0x0084;
		public const uint WM_SYSCOMMAND = 0x0112;

		public const uint SWP_NOSIZE = 0x0001;
		public const uint SWP_NOMOVE = 0x0002;
		public const uint SWP_NOZORDER = 0x0004;
		public const uint SWP_NOACTIVATE = 0x0010;

		public const int SC_SIZE = 0xF000;
		public const int SC_MOVE = 0xF010;

		// WM_NCHITTEST results: the eight resize edges/corners run HTLEFT (10) … HTBOTTOMRIGHT (17).
		public const int HTLEFT = 10;
		public const int HTBOTTOMRIGHT = 17;
		public const int HTBORDER = 18;

		private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

		[DllImport("comctl32.dll")]
		public static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc proc, UIntPtr id, UIntPtr refData);

		[DllImport("comctl32.dll")]
		public static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc proc, UIntPtr id);

		[DllImport("comctl32.dll")]
		public static extern IntPtr DefSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

		[DllImport("user32.dll")]
		private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

		[DllImport("user32.dll")]
		public static extern bool GetClientRect(IntPtr hWnd, out RECT rect);

		[DllImport("user32.dll")]
		public static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);

		[DllImport("user32.dll")]
		private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

		[DllImport("dwmapi.dll")]
		private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out RECT value, int size);

		/// <summary>
		/// Move and size <paramref name="hWnd"/> so its VISIBLE frame lands exactly on the given physical-pixel
		/// rect, by measuring the invisible border (outer rect vs DWM's extended frame bounds) and growing the
		/// outer rect by it.
		/// </summary>
		/// <remarks>
		/// ⚠️ SELF-CORRECTING, which is why the manager calls it twice. Before a window is shown DWM may report no
		/// frame bounds, so the border reads as zero and the first call lands a few pixels off; the call after
		/// Activate measures the real border and nudges it home in the same UI tick, before the first frame is
		/// composed. Once placed, calling it again is a no-op.
		/// </remarks>
		public static void PlaceVisibleFrame(IntPtr hWnd, int x, int y, int width, int height)
		{
			if (!GetWindowRect(hWnd, out var outer))
			{
				return;
			}

			if (DwmGetWindowAttribute(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var visible, Marshal.SizeOf<RECT>()) != 0)
			{
				visible = outer;
			}

			int left = visible.Left - outer.Left;
			int top = visible.Top - outer.Top;
			int right = outer.Right - visible.Right;
			int bottom = outer.Bottom - visible.Bottom;

			SetWindowPos(hWnd, IntPtr.Zero,
				x - left, y - top, width + left + right, height + top + bottom,
				SWP_NOZORDER | SWP_NOACTIVATE);
		}
	}
}
