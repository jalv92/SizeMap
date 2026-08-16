// SizeMap — Phase 0: "One Bitmap On The Chart"
//
// This file exists to retire the only two unknowns left in the environment, and then
// to be deleted. It draws no market data. It proves:
//
//   G1  a Direct2D bitmap can be created, updated EVERY FRAME with CopyFromMemory,
//       and blitted, from inside NinjaScript — without naming a single SharpDX.DXGI
//       type, because NinjaTrader.Custom.csproj does not reference that assembly.
//       The trick is BitmapProperties(RenderTarget.PixelFormat): the format is taken
//       from the target instead of being spelled out.
//   G7  ChartPanel coordinates, RenderTarget.PixelSize vs Size, DotsPerInch and
//       MaximumBitmapSize are what the architecture assumes on THIS machine.
//   +   SetZOrder(-1) really does put the raster behind the price bars.
//
// It also paints the real BEACON ramp, so the palette can be judged on the real
// monitor at the real gamma before any of it is committed to.
//
// The resource-lifetime guards below are not ceremony. A Direct2D resource that
// outlives its device is the number-one crash in NT8 SharpDX code, and it takes the
// platform down rather than just looking wrong.

#region Using declarations
using System;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
	public class SizeMapProbe : Indicator
	{
		// ---- BEACON, the 9 ramp stops. 0xAARRGGBB is the same 32-bit value as BGRA
		// ---- on little-endian x64 with B8G8R8A8_UNorm, so no byte swapping anywhere.
		private static readonly uint[] Stops =
		{
			0xFF232424, 0xFF103772, 0xFF1F4F94, 0xFF446B9E, 0xFF6187AE,
			0xFFA79F73, 0xFFC9B97A, 0xFFEFD472, 0xFFFFF49D
		};
		private const uint Ink  = 0xFF0E1013;
		private const uint Text = 0xFFE6EAEE;

		private readonly uint[] lut = new uint[256];

		private SharpDX.Direct2D1.Bitmap bmp;
		private object   rtSeen;            // identity guard: the chart has TWO render targets
		private int[]    px;
		private int      bw, bh;            // the bitmap's own size, == the panel's
		private int      frame;
		private bool     logged;

		private readonly Stopwatch sw = new Stopwatch();
		private double   emaMs;
		private int      frameLogCounter;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description                 = @"SizeMap Phase 0 — Direct2D bitmap probe. Draws no market data.";
				Name                        = "SizeMapProbe";
				Calculate                   = Calculate.OnBarClose;   // we render, we don't compute
				IsOverlay                   = true;
				IsChartOnly                 = true;
				DrawOnPricePanel            = false;
				DisplayInDataBox            = false;
				IsSuspendedWhileInactive    = true;

				DrawBehindBars              = true;
			}
			else if (State == State.Historical)
			{
				// Behind the bars. Bars sit at ZOrder 1; NinjaScript objects default to
				// 10001. -1 is under everything the platform draws.
				if (DrawBehindBars)
					SetZOrder(-1);

				BuildLut();
			}
			else if (State == State.Terminated)
			{
				ReleaseDeviceResources();
			}
		}

		// Called by NinjaTrader whenever the device is created, lost or recreated —
		// moving the chart to another monitor, a GPU reset, a resize. Anything holding
		// a device-dependent resource MUST let go here or the next frame reads freed
		// memory.
		public override void OnRenderTargetChanged()
		{
			ReleaseDeviceResources();
			rtSeen = RenderTarget;
		}

		// Bitmap(RenderTarget, Size2, BitmapProperties), found by signature so that no
		// SharpDX.DXGI type is ever named or resolved at compile time. Cached: the
		// lookup runs once per session, and bitmap creation itself only happens on a
		// resize or a device reset.
		private static System.Reflection.ConstructorInfo bitmapCtor;
		private static System.Reflection.ConstructorInfo BitmapCtor()
		{
			if (bitmapCtor != null) return bitmapCtor;
			foreach (var ci in typeof(SharpDX.Direct2D1.Bitmap).GetConstructors())
			{
				var p = ci.GetParameters();
				if (p.Length == 3
					&& p[0].ParameterType.Name == "RenderTarget"
					&& p[1].ParameterType.Name == "Size2")
				{
					bitmapCtor = ci;
					return bitmapCtor;
				}
			}
			// Loud, not silent: if SharpDX ever changes this shape we want to know at the
			// first frame, not through a blank chart.
			throw new InvalidOperationException(
				"SizeMap: Bitmap(RenderTarget, Size2, BitmapProperties) not found in SharpDX "
				+ typeof(SharpDX.Direct2D1.Bitmap).Assembly.GetName().Version);
		}

		private void ReleaseDeviceResources()
		{
			if (bmp != null)
			{
				try { bmp.Dispose(); } catch { }
				bmp = null;
			}
		}

		protected override void OnBarUpdate() { }

		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			// A full-panel raster means every click in the chart "hits" us. Without this
			// the hit-test pass would run the whole render.
			if (IsInHitTest) return;
			if (RenderTarget == null || RenderTarget.IsDisposed) return;
			if (ChartPanel == null) return;

			// The chart owns two render targets (the live window one and the one used for
			// snapshots). A bitmap made on one is invalid on the other, and the symptom is
			// D2DERR_WRONG_RESOURCE_DOMAIN or an access violation, not a blank chart.
			if (!ReferenceEquals(RenderTarget, rtSeen))
			{
				ReleaseDeviceResources();
				rtSeen = RenderTarget;
			}

			// Snapshot the panel rect: W/H can change mid-frame while the user drags a
			// panel divider.
			int px0 = ChartPanel.X, py0 = ChartPanel.Y, pw = ChartPanel.W, ph = ChartPanel.H;
			if (pw <= 0 || ph <= 0) return;

			// A throw inside a render pass is far more destructive than a wrong pixel.
			var oldAA = RenderTarget.AntialiasMode;
			try
			{
				sw.Restart();

				if (!logged) LogEnvironment(pw, ph);

				if (bmp == null || bw != pw || bh != ph)
				{
					ReleaseDeviceResources();
					bw = pw; bh = ph;
					px = new int[bw * bh];

					// Taking the format from the target means no SharpDX.DXGI type is ever
					// NAMED here — NinjaTrader.Custom.csproj references only SharpDX and
					// SharpDX.Direct2D1, so naming one is CS0246 in the NinjaScript Editor.
					var props = new SharpDX.Direct2D1.BitmapProperties(RenderTarget.PixelFormat);

					// But not naming one is not enough. `new Bitmap(...)` makes the compiler
					// load every Bitmap constructor to do overload resolution, and one of
					// them takes a SharpDX.DXGI.Surface — so the plain `new` is CS0012 even
					// though this file never mentions DXGI. Reflection skips overload
					// resolution entirely and needs no reference.
					//
					// ponytail: binds by signature at runtime. Ceiling — a SharpDX upgrade
					// that changed this constructor would fail at load instead of at compile.
					// Upgrade path: add bin/SharpDX.DXGI.dll in Editor > References and this
					// becomes a plain `new`.
					bmp = (SharpDX.Direct2D1.Bitmap)BitmapCtor().Invoke(
						new object[] { RenderTarget, new SharpDX.Size2(bw, bh), props });
				}

				Paint();

				// Rebuilt and re-uploaded EVERY frame on purpose: creating a bitmap once and
				// blitting it forever would prove nothing about the path the real indicator
				// uses 4 times a second.
				bmp.CopyFromMemory(px, bw * 4);

				RenderTarget.AntialiasMode = SharpDX.Direct2D1.AntialiasMode.Aliased;
				// SharpDX.RectangleF, NOT SharpDX.Mathematics.Interop.RawRectangleF: the Raw*
				// types live in SharpDX.Mathematics.dll, which NinjaTrader.Custom.csproj does
				// not reference either. Same trap as DXGI, different assembly.
				RenderTarget.DrawBitmap(
					bmp,
					new SharpDX.RectangleF(px0, py0, pw, ph),
					1f,
					SharpDX.Direct2D1.BitmapInterpolationMode.NearestNeighbor);

				sw.Stop();
				double ms = sw.Elapsed.TotalMilliseconds;
				emaMs = emaMs == 0 ? ms : emaMs * 0.9 + ms * 0.1;
				if (++frameLogCounter % 20 == 0)
					Print(string.Format("SizeMapProbe  frame {0}  {1:0.00} ms (ema {2:0.00})  panel {3}x{4}",
						frame, ms, emaMs, pw, ph));

				frame++;
			}
			catch (Exception ex)
			{
				Print("SizeMapProbe render error: " + ex);
				ReleaseDeviceResources();
			}
			finally
			{
				// Restore what was READ, not a hardcoded default — NinjaTrader draws after
				// us and inherits whatever state we leave behind.
				RenderTarget.AntialiasMode = oldAA;
			}
		}

		// ---------------------------------------------------------------- the picture
		private void Paint()
		{
			uint bg = Stops[0];
			for (int i = 0; i < px.Length; i++) px[i] = unchecked((int)bg);

			// 1. The full ramp, left to right. This is the thing to judge: the steps must
			//    be evenly spaced to the eye all the way across, with no band that reads
			//    lighter than the one to its right.
			int rampTop = bh / 6, rampH = Math.Max(24, bh / 8);
			for (int y = rampTop; y < rampTop + rampH && y < bh; y++)
				for (int x = 0; x < bw; x++)
					px[y * bw + x] = unchecked((int)lut[(x * 255) / Math.Max(1, bw - 1)]);

			// 2. The nine stops as discrete blocks, so a bad step is obvious rather than
			//    smoothed over by the interpolation above.
			int blkTop = rampTop + rampH + 10, blkH = Math.Max(16, bh / 14);
			int blkW = bw / 9;
			for (int s = 0; s < 9; s++)
				for (int y = blkTop; y < blkTop + blkH && y < bh; y++)
					for (int x = s * blkW; x < (s + 1) * blkW && x < bw; x++)
						px[y * bw + x] = unchecked((int)Stops[s]);

			// 3. The grammar, at real pixel sizes: a solid wall band with its 1 px ink
			//    groove, and remembered bands at three dash duties. Q3 on the spec is
			//    "is one pixel of groove enough on your monitor" — this is that question.
			int rowH = 9;
			int gTop = blkTop + blkH + 30;
			DrawBand(gTop, rowH, lut[250], true, 8);                 // live wall, grooved
			DrawBand(gTop + rowH + 22, rowH, lut[210], false, 8);    // remembered, conf 1.0
			DrawBand(gTop + 2 * (rowH + 22), rowH, lut[210], false, 4);
			DrawBand(gTop + 3 * (rowH + 22), rowH, lut[210], false, 2);

			// 4. The touch trace over the ramp: 1 px core, 1 px ink casing either side.
			//    The point to check is that it stays visible over EVERY stop, including
			//    the near-white one at the right where a bare white line would vanish.
			int traceY = rampTop + rampH / 2;
			for (int x = 0; x < bw; x++)
			{
				int yy = traceY + (int)(6 * Math.Sin((x + frame * 2) * 0.01));
				Put(x, yy - 1, Ink); Put(x, yy + 1, Ink); Put(x, yy, Text);
			}

			// 5. The liveness marker. If this column is not sweeping, CopyFromMemory is
			//    not running per frame and the probe is proving nothing.
			int scan = (frame * 3) % bw;
			for (int y = 0; y < bh; y++) Put(scan, y, Text);
		}

		private void DrawBand(int top, int h, uint colour, bool solid, int dashOn)
		{
			int x0 = bw / 8, x1 = bw - bw / 8;
			if (solid)
			{
				for (int y = top; y < top + h; y++)
					for (int x = x0; x < x1; x++) Put(x, y, colour);
				for (int x = x0; x < x1; x++) { Put(x, top - 1, Ink); Put(x, top + h, Ink); }
				for (int y = top - 1; y <= top + h; y++) Put(x0, y, Ink);
			}
			else
			{
				for (int x = x0; x < x1; x++)
					if (x % 8 < dashOn) { Put(x, top - 1, colour); Put(x, top + h, colour); }
			}
		}

		private void Put(int x, int y, uint c)
		{
			if (x >= 0 && x < bw && y >= 0 && y < bh) px[y * bw + x] = unchecked((int)c);
		}

		private void BuildLut()
		{
			int[] idx = { 0, 32, 64, 96, 128, 160, 192, 224, 255 };
			for (int i = 0; i < 256; i++)
			{
				int s = 0;
				while (s < 7 && i >= idx[s + 1]) s++;
				int span = idx[s + 1] - idx[s];
				double t = span == 0 ? 0 : (double)(i - idx[s]) / span;
				uint c = LerpRgb(Stops[s], Stops[s + 1], Math.Min(1.0, t));
				// Fade the foot into the background so "1 lot resting" is invisible rather
				// than grey noise.
				lut[i] = i < 24 ? LerpRgb(Stops[0], c, i / 24.0) : c;
			}
			lut[0] = Stops[0];
		}

		private static uint LerpRgb(uint a, uint b, double t)
		{
			uint ar = (a >> 16) & 0xFF, ag = (a >> 8) & 0xFF, ab = a & 0xFF;
			uint br = (b >> 16) & 0xFF, bg2 = (b >> 8) & 0xFF, bb = b & 0xFF;
			uint r = (uint)Math.Round(ar + (br - (double)ar) * t);
			uint g = (uint)Math.Round(ag + (bg2 - (double)ag) * t);
			uint bl = (uint)Math.Round(ab + (bb - (double)ab) * t);
			return 0xFF000000u | (r << 16) | (g << 8) | bl;
		}

		private void LogEnvironment(int pw, int ph)
		{
			logged = true;
			try
			{
				Print("---- SizeMapProbe environment ----");
				Print("RenderTarget type      : " + RenderTarget.GetType().FullName);
				Print("MaximumBitmapSize      : " + RenderTarget.MaximumBitmapSize);
				Print("DotsPerInch            : " + RenderTarget.DotsPerInch.Width + " x " + RenderTarget.DotsPerInch.Height);
				Print("RenderTarget.Size      : " + RenderTarget.Size.Width + " x " + RenderTarget.Size.Height);
				Print("RenderTarget.PixelSize : " + RenderTarget.PixelSize.Width + " x " + RenderTarget.PixelSize.Height);
				// NOT RenderTarget.PixelFormat.Format — that field's type is SharpDX.DXGI.Format
				// and merely reading it is CS0012 without the reference. ToString() on the
				// struct gives the same information and names nothing.
				Print("PixelFormat            : " + RenderTarget.PixelFormat.ToString());
				Print("ChartPanel X,Y,W,H     : " + ChartPanel.X + "," + ChartPanel.Y + "," + pw + "," + ph);
				Print("ZOrder requested       : " + (DrawBehindBars ? "-1 (behind bars)" : "default (in front)"));
				// 1 DIP == 1 device pixel is what makes ChartPanel coords usable directly in
				// the blit rect. If these differ, the whole coordinate story needs scaling.
				Print("DIP == device pixel?   : " +
					(Math.Abs(RenderTarget.Size.Width - RenderTarget.PixelSize.Width) < 0.5 ? "YES" : "NO — scale the blit rect"));
				Print("----------------------------------");
			}
			catch (Exception ex) { Print("SizeMapProbe env log failed: " + ex.Message); }
		}

		#region Properties
		[NinjaScriptProperty]
		[Display(Name = "Draw behind bars", Description = "SetZOrder(-1). Turn off to see the raster paint over the candles instead.", Order = 1, GroupName = "SizeMap Probe")]
		public bool DrawBehindBars { get; set; }
		#endregion
	}
}
