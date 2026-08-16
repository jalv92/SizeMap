// SizeMapHeat — the only file in SizeMap that touches NinjaTrader.
//
// It contains no heatmap logic. It adapts events in (MarketDepthEventArgs -> DepthEvent ->
// BookMirror + ColumnRing) and pixels out (ColumnRing -> Rasterizer -> one DrawBitmap). Every
// decision about what the picture MEANS lives in engine/, which is portable, tested and
// harness-renderable; everything here is platform plumbing that cannot be tested off-platform.
//
// The guards below are not ceremony. Each one is a bug that has already been paid for once —
// in SizeMapProbe.cs on this machine, or in Trading-radar's RadarTab.cs on live tape.
//
// Phase 1 deliberately has no on-raster text: the 5x7 bitmap font is Phase 2, so the frame-time
// HUD goes to the Output window instead. That is also why the refusal paths below Print rather
// than draw — see Refuse().

#region Using declarations
using System;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Threading;
using NinjaTrader.Cbi;      // Operation (the depth Add/Update/Remove enum) lives here, not in Data
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using SizeMap.Engine;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
	public class SizeMapHeat : Indicator
	{
		// ================= depth thread — everything here under _engineLock =================
		private readonly object _engineLock = new object();
		private BookMirror      _book;
		private DepthBaseline   _depthBase;
		private ColumnRing      _ring;
		private SizeMapRecorder _rec;
		private double          _tick = 0.25;
		private double          _barMs;              // 0 = the bar type carries no time model

		private DateTime _lastEngineRun   = DateTime.MinValue;
		private DateTime _lastDepthSample = DateTime.MinValue;

		// Ported verbatim from RadarTab.cs 105-106. These three numbers ARE the replay handling.
		private const double EngineIntervalMs      = 50;      // ~20 Hz forward throttle
		private const double ReplayResetBackwardMs = 2000;    // real rewinds jump seconds-minutes
		private const double ReplayResetForwardMs  = 60000;   // bigger than any real quiet-tape gap

		// ---- depth thread -> UI thread. Only these, only through Volatile, never a lock. ----
		private long _nowTicks;                 // market time of the newest event
		private int  _s0 = 8, _sCap = 320;      // the live ramp anchors, in lots
		private int  _levelsSeen;               // max levels actually observed this session
		private int  _epochs;                   // replay rewinds / feed discontinuities handled

		// ================= UI thread only =================
		private readonly Palette   _palette = new Palette();
		private readonly Stopwatch _sw      = new Stopwatch();
		private SharpDX.Direct2D1.Bitmap _bmp;
		private object _rtSeen;                 // identity guard: the chart owns TWO render targets
		private int[]  _px;
		private int    _bmpW, _bmpH;
		private bool   _zReissued;
		private string _lastRefusal;
		private double _emaMs;
		private int    _frame;
		private const int HudEveryFrames = 20;  // ~5 s at NT8's 4 fps repaint cap

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Name                     = "SizeMapHeat";
				Description              = @"Depth liquidity heatmap: resting size in tick x 250 ms cells, drawn behind the bars.";
				Calculate                = Calculate.OnEachTick;
				IsOverlay                = true;    // share the bars' ChartScale
				IsChartOnly              = true;
				DrawOnPricePanel         = false;   // SetZOrder(-N) has been reported not to take
				                                    // effect when combined with it; we use zero Draw.*
				DisplayInDataBox         = false;
				IsAutoScale              = false;   // a heatmap must never move the price scale
				ScaleJustification       = ScaleJustification.Right;
				// Collecting must NOT stop when the tab is hidden, or the ring gets holes that look
				// exactly like a quiet market and cannot be told apart afterwards.
				IsSuspendedWhileInactive = false;

				MinutesOfHistory         = 30;
				RecordRaw                = true;
			}
			else if (State == State.DataLoaded)
			{
				// Everything is built here, before the first market event can arrive, and nothing is
				// ever allocated again on either hot path. State.Historical runs AFTER this, so the
				// ring and the palette exist by the time the z-order is set.
				_tick      = TickSize > 0 ? TickSize : 0.25;
				_barMs     = BarMsOf(BarsPeriod);
				_book      = new BookMirror(_tick, TimeSpan.FromSeconds(30));
				_depthBase = new DepthBaseline(4096);
				_ring      = new ColumnRing(Math.Max(2, MinutesOfHistory * 60 * 1000 / ColumnRing.BucketMs), 48);
				SeedFromSnapshot();

				if (RecordRaw)
				{
					_rec = new SizeMapRecorder(Instrument.MasterInstrument.Name, _tick, IsPlayback());
					// Called from the drain thread; Print marshals to the UI dispatcher and never
					// calls back into the recorder, which is the contract that file documents.
					_rec.Log = msg => Print("SizeMapHeat rec: " + msg);
					_rec.Start();
				}
			}
			else if (State == State.Historical)
			{
				// Bars sit at ZOrder 1, NinjaScript objects default to 10001. -1 is the band below
				// everything the platform draws, and it is only honoured from State.Historical.
				SetZOrder(-1);
			}
			else if (State == State.Terminated)
			{
				if (_rec != null) { _rec.Stop(); _rec = null; }   // flushes on its own thread
				ReleaseDeviceResources();
			}
		}

		protected override void OnBarUpdate() { }   // nothing here is bar-driven

		// ================= INSTRUMENT DEPTH THREAD =================
		// No Print. No LINQ. No file IO. No chart access. No DateTime.Now — every timestamp is
		// e.Time, because in accelerated Playback market time and wall time are minutes apart and
		// every window in this file is expressed in market time.
		//
		// Allocation: the per-event path allocates nothing. ONE call does — DepthBaseline.EndBatch,
		// once per market-second, measured at 0.203 ms and 32 KB. At 1x that is invisible; at 500x
		// Replay it is ~10% of a core and ~15 MB/s of Gen0 on a thread shared with every chart and
		// DOM on this instrument. Said plainly here because "zero allocation" in a comment is what
		// the next reader will trust.
		// ponytail: left as is. Ceiling — high-speed Replay only. Upgrade path — a ring of reusable
		// buffers in DepthBaseline, if a fast Replay ever stutters.

		protected override void OnMarketDepth(MarketDepthEventArgs e)
		{
			if (_ring == null) return;

			// RESET FIRST, above the price guard. A reset carries no meaningful price, so if the
			// `Price <= 0` test runs first the reconnect book-wipe becomes dead code and the map
			// goes silently stale after every reconnect. That bug was found and fixed once already
			// (RadarTab.cs 581) — this ordering is the fix.
			if (e.IsReset)
			{
				DepthEvent rs = new DepthEvent { IsReset = true, Time = e.Time };
				lock (_engineLock)
				{
					if (e.Instrument != Instrument) return;
					_book.ApplyDepth(rs);
					if (_rec != null) _rec.OnDepth(rs);
				}
				return;
			}
			if (e.Price <= 0) return;

			DepthOp op = e.Operation == Operation.Add    ? DepthOp.Add
			           : e.Operation == Operation.Update ? DepthOp.Update
			                                             : DepthOp.Remove;
			DepthEvent de = new DepthEvent
			{
				Side     = e.MarketDataType == MarketDataType.Ask ? Side.Ask : Side.Bid,
				Op       = op,
				Position = e.Position,
				Price    = e.Price,
				Volume   = e.Volume,
				Time     = e.Time,
				IsReset  = false
			};

			lock (_engineLock)
			{
				// Stale event from a PRIOR instrument mid-switch — dropped before it touches the book.
				if (e.Instrument != Instrument) return;
				_book.ApplyDepth(de);
				_ring.Accumulate(e.Time.Ticks, ColumnRing.RowFor(de.Price, _tick), de.Side, de.Volume);

				// The inside market, stored per column. This is also the seam the bid/ask touch trace
				// needs: it is the trace's data, recorded here for free because the book is already hot.
				DepthLevel bb, ba;
				_ring.SetInside(e.Time.Ticks,
					_book.TryBestBid(out bb) ? ColumnRing.RowFor(bb.Price, _tick) : ColumnRing.NoRow,
					_book.TryBestAsk(out ba) ? ColumnRing.RowFor(ba.Price, _tick) : ColumnRing.NoRow);

				if (_rec != null) _rec.OnDepth(de);
				Volatile.Write(ref _nowTicks, e.Time.Ticks);
				MaybeRunEngine(e.Time);
			}
		}

		protected override void OnMarketData(MarketDataEventArgs e)
		{
			if (_ring == null) return;
			if (e.MarketDataType != MarketDataType.Last || e.Price <= 0) return;

			TradeEvent te = new TradeEvent { Price = e.Price, Volume = e.Volume, Time = e.Time };
			lock (_engineLock)
			{
				if (e.Instrument != Instrument) return;
				// Read the inside BEFORE the trade is applied: the aggressor is classified against
				// the quote prevailing at trade time, not the one the trade just changed.
				DepthLevel bb, ba;
				double bid = _book.TryBestBid(out bb) ? bb.Price : 0;
				double ask = _book.TryBestAsk(out ba) ? ba.Price : 0;
				_book.ApplyTrade(te);
				if (_rec != null) _rec.OnTrade(te, ask > 0 ? e.Price >= ask : e.Price > bid);
				Volatile.Write(ref _nowTicks, e.Time.Ticks);
				MaybeRunEngine(e.Time);
			}
		}

		// The clock-discontinuity guard, ported from RadarTab.cs 628-665. Three branches, and their
		// ORDER is the whole point. Javier develops this entire project in Market Replay, so this is
		// not an edge case: without it the first drag of the Playback slider freezes the picture and
		// reads as a render bug.
		private void MaybeRunEngine(DateTime now)
		{
			if (_lastEngineRun != DateTime.MinValue)
			{
				double dMs = (now - _lastEngineRun).TotalMilliseconds;

				// (1) far BACKWARD (rewind/restart) or far FORWARD (scrub-ahead, session rollover —
				//     which would otherwise paint a stale book as live). Rebuild, then fall through.
				if (dMs < -ReplayResetBackwardMs || dMs > ReplayResetForwardMs)
					HandleReplayReset(now);
				// (2) small BACKWARD step (sub-2 s rewind, or an out-of-order feed tick): don't run,
				//     but DO re-base the clock down to `now`. Leaving _lastEngineRun at the forward
				//     high-water mark and early-returning froze the engine until replay time climbed
				//     back past it — that was the bug, and an early return alone reintroduces it.
				else if (dMs < 0) { _lastEngineRun = now; return; }
				// (3) normal forward throttle; _lastEngineRun stays at the last ACTUAL run.
				else if (dMs < EngineIntervalMs) return;
			}
			_lastEngineRun = now;

			// 1 Hz, NOT per engine run: at 20 Hz this resamples the same resting book 20x and just
			// autocorrelates itself into a confident wrong percentile (ADR 2026-07-03).
			if (_lastDepthSample == DateTime.MinValue || (now - _lastDepthSample).TotalSeconds >= 1.0)
			{
				_lastDepthSample = now;
				System.Collections.Generic.IReadOnlyList<DepthLevel> b = _book.Levels(Side.Bid);
				System.Collections.Generic.IReadOnlyList<DepthLevel> a = _book.Levels(Side.Ask);
				for (int i = 0; i < b.Count; i++) _depthBase.Add(b[i].Volume);
				for (int i = 0; i < a.Count; i++) _depthBase.Add(a[i].Volume);
				_depthBase.EndBatch();
				PublishScale(b.Count > a.Count ? b.Count : a.Count);
			}
		}

		// NT8 exposes no "replay was reset" event, so a large jump in e.Time is the most reliable
		// mechanism-agnostic signal. REBUILD, never Clear: a cleared object keeps whatever internal
		// bookkeeping its Clear() forgot, and that is how pre-rewind state survives into replayed
		// history it never saw.
		private void HandleReplayReset(DateTime now)
		{
			_book = new BookMirror(_tick, TimeSpan.FromSeconds(30));
			SeedFromSnapshot();
			_depthBase.Reset();                 // rewound history must not inherit the old distribution
			_lastDepthSample = DateTime.MinValue;
			_ring.Reset(now.Ticks);             // publishes -1 first, so a mid-wipe reader draws nothing
			if (_rec != null) _rec.OnEpochBreak(now);   // close the file; never splice two tape epochs
			Volatile.Write(ref _epochs, Volatile.Read(ref _epochs) + 1);
			// No Print here: this runs on the depth thread and Print marshals to the UI dispatcher.
			// The counter above surfaces it in the HUD line instead.
		}

		// Prime the ladder from the platform's own snapshot so a chart opened mid-session has a book
		// immediately instead of waiting for every level to be quoted again (RadarTab.cs 511-520).
		private void SeedFromSnapshot()
		{
			try
			{
				if (Instrument == null || Instrument.MarketDepth == null) return;
				System.Collections.Generic.List<DepthLevel> bids = new System.Collections.Generic.List<DepthLevel>();
				System.Collections.Generic.List<DepthLevel> asks = new System.Collections.Generic.List<DepthLevel>();
				for (int i = 0; i < Instrument.MarketDepth.Bids.Count; i++)
					bids.Add(new DepthLevel { Price = Instrument.MarketDepth.Bids[i].Price, Volume = Instrument.MarketDepth.Bids[i].Volume });
				for (int i = 0; i < Instrument.MarketDepth.Asks.Count; i++)
					asks.Add(new DepthLevel { Price = Instrument.MarketDepth.Asks[i].Price, Volume = Instrument.MarketDepth.Asks[i].Volume });
				_book.ResetFromSnapshot(bids, asks);
			}
			catch (Exception) { }   // no snapshot yet is normal; it is not a reason to fail construction
		}

		private void PublishScale(int levels)
		{
			if (levels > Volatile.Read(ref _levelsSeen)) Volatile.Write(ref _levelsSeen, levels);

			long p85 = _depthBase.P85;
			if (p85 <= 0) return;   // still warming up (< 300 samples): keep the compiled defaults

			// ponytail: visual-spec §2.4 anchors s0 on P50, and DepthBaseline — vendored from
			// Trading-radar and re-synced by diff — only exposes P85. P85/4 reproduces the spec's
			// own worked NQ example (P85 40 -> s0 8..10). Ceiling: on an instrument whose level
			// sizes are less skewed the ramp foot sits too high and small levels wash out. Upgrade
			// path: the HUD prints S0 and CAP every 20 frames, so the recorded corpus settles the
			// ratio and it becomes one number here — not a percentile fork of a vendored file.
			int s0  = (int)Math.Max(1, p85 / 4);
			int cap = (int)Math.Max(8 * s0, 8 * p85);
			Volatile.Write(ref _s0, s0);
			Volatile.Write(ref _sCap, cap);
		}

		// ================= UI THREAD =================

		// Fires on device create AND destroy — on destroy RenderTarget is already null, which is why
		// creation cannot live here and the identity compare in OnRender is the belt.
		public override void OnRenderTargetChanged()
		{
			ReleaseDeviceResources();
			_rtSeen = RenderTarget;
		}

		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			// A full-panel raster means every click in the chart "hits" us; without this the hit-test
			// pass would run the whole render and a double-click would open our properties dialog.
			if (IsInHitTest) return;
			if (RenderTarget == null || RenderTarget.IsDisposed) return;
			if (chartControl == null || chartScale == null || ChartBars == null || ChartPanel == null) return;
			if (_ring == null) return;

			// The chart owns two render targets (the window one and the WIC one used for hit-test,
			// taskbar thumbnails and resize snapshots). A bitmap made on one is invalid on the other,
			// and the symptom is D2DERR_WRONG_RESOURCE_DOMAIN or an access violation, not a blank chart.
			if (!ReferenceEquals(RenderTarget, _rtSeen))
			{
				ReleaseDeviceResources();
				_rtSeen = RenderTarget;
			}

			// Snapshot the panel rect: W/H can change between two reads inside one frame while the
			// user drags a panel divider.
			int panelX = ChartPanel.X, panelY = ChartPanel.Y, pw = ChartPanel.W, ph = ChartPanel.H;
			if (pw <= 0 || ph <= 0) return;

			// Restore what was READ, not a hardcoded default: the D2D default is PerPrimitive, the
			// target is shared, and NinjaTrader draws after us.
			SharpDX.Direct2D1.AntialiasMode oldAA = RenderTarget.AntialiasMode;
			try
			{
				_sw.Restart();

				// Known first-apply bug: the object can render on top until the chart is reloaded.
				// SetZOrder is idempotent, so re-issuing it once costs nothing.
				if (!_zReissued && State == State.Realtime) { _zReissued = true; SetZOrder(-1); }

				string why = Refusal(chartControl, chartScale, pw, ph);
				if (why != null) { Refuse(why); return; }
				_lastRefusal = null;

				long now = Volatile.Read(ref _nowTicks);
				if (now == 0) return;                       // no depth yet: nothing to claim
				int published = _ring.PublishedIndex;
				if (published < 0) return;                  // just after a rewind. Drawing nothing is the truth.

				// Ramp stop 0 IS "no resting size", so it has to be whatever NT8 actually painted.
				// Measured on this machine: ~#1F1F1F, not the #232424 the probe hardcodes. Both calls
				// return immediately when nothing moved.
				_palette.Rebuild(ChartBackground(chartControl));
				_palette.SetScale(Volatile.Read(ref _s0), Volatile.Read(ref _sCap));

				RasterView view = BuildView(chartControl, chartScale, panelX, panelY, ph, now);
				if (!EnsureBitmap(pw, ph)) return;

				Rasterizer.Rasterize(_ring, _px, pw, ph, view, _palette);
				_bmp.CopyFromMemory(_px, pw * 4);

				RenderTarget.AntialiasMode = SharpDX.Direct2D1.AntialiasMode.Aliased;
				// SharpDX.RectangleF, NOT SharpDX.Mathematics.Interop.RawRectangleF: the Raw* types
				// live in a third assembly NinjaTrader.Custom.csproj does not reference either.
				// NearestNeighbor because Linear would invent resolution between 250 ms columns and
				// 1-tick rows — and with a 1:1 dest rect it is a pure copy anyway.
				RenderTarget.DrawBitmap(
					_bmp,
					new SharpDX.RectangleF(panelX, panelY, pw, ph),
					1f,
					SharpDX.Direct2D1.BitmapInterpolationMode.NearestNeighbor);
				// That DrawBitmap is the ONLY post-blit primitive, by design. Everything else is an
				// int write into _px.

				_sw.Stop();
				Hud(pw, ph, view, _sw.Elapsed.TotalMilliseconds);
			}
			catch (Exception ex)
			{
				// A throw escaping OnRender disables the indicator and is far more destructive than
				// a wrong pixel.
				Print("SizeMapHeat render error: " + ex);
				ReleaseDeviceResources();
			}
			finally
			{
				// Its own try/catch, because the most likely cause of a throw in the body above is
				// the render target being disposed mid-frame — and then this line dereferences that
				// same disposed target and throws UNHANDLED, out of the finally, defeating the catch
				// that exists to prevent exactly that. A finally that can throw is not a guard.
				try { RenderTarget.AntialiasMode = oldAA; } catch { }
			}
		}

		// Everything the projection needs, rebuilt from scratch every frame. Nothing derived from a
		// previous frame survives, so scroll, zoom, resize and bar-spacing changes need no
		// invalidation path — there is no cached projection to invalidate.
		private RasterView BuildView(ChartControl cc, ChartScale cs, int panelX, int panelY, int ph, long nowTicks)
		{
			// ROWS: ONE anchor per frame. A per-row GetYByValue returns int, so consecutive tick rows
			// come out 13, 14, 13, 14 px and drift against NT8's own gridlines — that specific defect
			// is why the platform's own depth map reads as smeared slabs.
			// NOT GetPixelsForDistance(_tick). It is typed float but returns a whole number of
			// pixels — measured on this machine: 4.000 where the true height of a tick was 4.439,
			// 5.000 where it was 4.590, 3.000 where it was 3.168. A sub-pixel residual per tick is
			// invisible on one row and lethal across a panel, because the row map is one anchor plus
			// a constant: the error accumulates linearly and put the heat up to 89 px (20 ticks)
			// away from the candles at ordinary zooms. Every PROJ err reading matched that
			// prediction to within a pixel.
			//
			// So measure the tick over the WHOLE visible range instead of asking for one tick.
			// GetYByValue also returns int, but one pixel of quantisation spread over ~1050 is a
			// 0.1% error rather than a 10% one.
			double span   = cs.MaxValue - cs.MinValue;
			int    yBot   = cs.GetYByValue(cs.MinValue);
			int    yTop   = cs.GetYByValue(cs.MaxValue);
			float pxPerTick = (span > 0 && yBot > yTop)
				? (float)((yBot - yTop) * _tick / span)
				: cs.GetPixelsForDistance(_tick);      // degenerate scale: fall back rather than divide by zero
			if (pxPerTick <= 0) pxPerTick = 1f;
			int   anchorRow = (int)Math.Round(cs.MinValue / _tick);
			float anchorY   = cs.GetYByValue(anchorRow * _tick) - panelY;   // panel-local: the raster is blitted at (panelX, panelY)

			// COLUMNS: from the bar PITCH, never GetXByTime — that is slot-based, so every timestamp
			// inside a bar returns the same x and 250 ms resolution silently collapses to bar resolution.
			int i0 = ChartBars.FromIndex, i1 = ChartBars.ToIndex;
			int xLastCentre = cc.GetXByBarIndex(ChartBars, i1);
			float pitch = i1 > i0
				? (xLastCentre - cc.GetXByBarIndex(ChartBars, i0)) / (float)(i1 - i0)
				: cc.GetBarPaintWidth(ChartBars);
			if (pitch <= 0) pitch = 1f;

			float pxPerMs = (float)(pitch / _barMs);

			// GetXByBarIndex returns the bar CENTRE, and NT8 stamps a time-based bar with its period
			// END, so the bar's own timestamp sits at its RIGHT edge.
			DateTime lastClose = ChartBars.Bars.GetTime(i1);
			float    xRight    = xLastCentre + pitch * 0.5f - panelX;

			// NowX is the right edge of the newest 250 ms bucket, not "now": the rasterizer lays every
			// older column off it at PxPerBucket.
			//
			// ponytail: time -> x is linear across the visible window. Ceiling — a session break
			// inside the ring's 30 minutes (the daily maintenance halt) would shift columns older
			// than the gap by the gap's width. Upgrade path — RasterView already isolates this in six
			// fields; swap NowX/PxPerBucket for the SlotTicks[]/SlotX[] bar-edge pair and only
			// Rasterizer.DrawColumns changes.
			long  bucketEnd = (ColumnRing.BucketOf(nowTicks) + 1) * ColumnRing.BucketTicks;
			float nowX      = xRight + (float)((bucketEnd - lastClose.Ticks) / (double)TimeSpan.TicksPerMillisecond) * pxPerMs;

			// SELF-CHECK, and it costs two calls a frame. The row map is ONE anchor plus a constant
			// pxPerTick, which is only true if NinjaTrader's own price scale is exactly linear over
			// the visible range. Measured on a zoomed-out ES chart, the heat sat up to 96 px BELOW
			// the candles — an error that grew linearly away from the anchor, which is the signature
			// of a wrong pxPerTick rather than a wrong anchor. Rather than infer that from
			// screenshots, ask NT8 where a price goes and compare. If _projErr is not ~0 the
			// projection is lying and the whole raster is misplaced.
			double probeP = cs.MinValue + (cs.MaxValue - cs.MinValue) * 0.85;
			int    ntY    = cs.GetYByValue(probeP) - panelY;
			float  ourY   = anchorY - ((float)Math.Round(probeP / _tick) - anchorRow) * pxPerTick;
			_projErr      = ourY - ntY;
			_projPxTick   = pxPerTick;
			_projSpan     = cs.MaxValue - cs.MinValue;

			return new RasterView(anchorRow, anchorY, pxPerTick, nowTicks, nowX, pxPerMs * ColumnRing.BucketMs);
		}

		private float  _projErr;      // our row map minus NT8's, in px, at 85% up the visible range
		private float  _projPxTick;
		private double _projSpan;

		// The projections above are only true on a linear price axis and an equidistant, time-based
		// x-axis. Where they are not, SizeMap draws nothing: a plausible lie about where liquidity sat
		// is worse than an empty panel, because the empty panel is the only one you can't trade off.
		private string Refusal(ChartControl cc, ChartScale cs, int pw, int ph)
		{
			if (cs.Properties.YAxisScalingType == YAxisScalingType.Logarithmic)
				return "log price axis — the single-anchor row map is linear and would misplace every row";
			if (cc.BarSpacingType == BarSpacingType.TimeBased)
				return "TimeBased bar spacing — x is not linear in bar slots and GetSlotIndexByTime throws";
			// The ramp is monotone only against a ground darker than its first stop. Above that,
			// "nothing resting" would render brighter than "8 lots resting" — the ramp lying about
			// its own direction. Refusing is honest; painting our own dark plate over a light chart
			// is not, because NT8 then draws its light-theme gridlines and dark candles on top of it.
			if (ChartBackground(cc) == 0)
				return "chart background is too light (or is a gradient) for the one dark ramp v1 ships — use a dark theme";
			if (_barMs <= 0)
				return "bar type " + BarsPeriod.BarsPeriodType + " has no intrabar time model — use Second or Minute bars";
			if (ChartBars.Bars == null || ChartBars.Bars.Count == 0 || ChartBars.ToIndex < 0)
				return "no bars loaded";
			if (pw > RenderTarget.MaximumBitmapSize || ph > RenderTarget.MaximumBitmapSize)
				return "panel " + pw + "x" + ph + " exceeds MaximumBitmapSize " + RenderTarget.MaximumBitmapSize;
			return null;
		}

		// ponytail: the refusal reason goes to the Output window, not onto the chart. Ceiling — a
		// refusing chart looks identical to a quiet one unless you open Output. Upgrade path — the
		// 5x7 bitmap font is Phase 2's first piece; when it lands this becomes one Text() call into
		// _px and the post-blit primitive count stays at exactly one.
		private void Refuse(string why)
		{
			if (why == _lastRefusal) return;
			_lastRefusal = why;
			Print("SizeMapHeat: drawing nothing — " + why);
			// Also say it ON the chart. A refusing panel is indistinguishable from a broken one, and
			// Javier's own projects run 150-tick and 30-second charts — dropping SizeMap on a tick
			// chart is the single most likely first contact with this indicator. The Output window
			// is not somewhere he has any reason to be looking.
			// This is the refusal path, so no bitmap is blitted and the "exactly one post-blit
			// primitive" budget is untouched.
			// ponytail: Draw.TextFixed, not the 5x7 raster font, which is Phase 2's first piece.
			try
			{
				NinjaTrader.NinjaScript.DrawingTools.Draw.TextFixed(
					this, "SizeMapRefusal", "SizeMap — " + why,
					NinjaTrader.NinjaScript.DrawingTools.TextPosition.BottomLeft,
					System.Windows.Media.Brushes.Gray, ChartControl.Properties.LabelFont,
					System.Windows.Media.Brushes.Transparent,
					System.Windows.Media.Brushes.Transparent, 0);
			}
			catch { }   // a drawing failure must never be worse than the thing it explains
		}

		// Returns 0 when the theme cannot carry the ramp, so the caller refuses instead of drawing.
		// Substituting our own dark ground here — the previous behaviour — meant filling the panel
		// with LUT index 0, i.e. painting an opaque near-black plate over a light chart and then
		// letting NT8 draw its light-theme gridlines and dark candles on top of it. Unreadable. And
		// the threshold was 0.18 while the ramp's actual precondition is Palette.MaxBackgroundLuminance
		// (0.0406), so every grey theme in between inverted the ramp silently.
		// ponytail: a light-theme ramp is a whole second palette, audited separately. Refusing is the
		// honest placeholder. Upgrade path — build the second ramp when Javier asks for a light chart.
		private uint ChartBackground(ChartControl cc)
		{
			uint bg = SolidBgra(cc.Properties.ChartBackground, 0);
			if (bg == 0) return 0;                                  // gradient/image: luminance unknowable
			return Palette.BackgroundIsUsable(bg) ? bg : 0;
		}

		private static uint SolidBgra(System.Windows.Media.Brush b, uint fallback)
		{
			System.Windows.Media.SolidColorBrush s = b as System.Windows.Media.SolidColorBrush;
			if (s == null) return fallback;   // gradient or image background: luminance is unknowable
			System.Windows.Media.Color c = s.Color;
			byte a = (byte)Math.Round(c.A * s.Opacity);   // brush Opacity is separate from Color.A
			return ((uint)a << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
		}

		private static double RelLum(uint argb)
		{
			return 0.2126 * Lin((argb >> 16) & 0xFF) + 0.7152 * Lin((argb >> 8) & 0xFF) + 0.0722 * Lin(argb & 0xFF);
		}

		private static double Lin(uint ch)
		{
			double v = ch / 255.0;
			return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
		}

		private static double BarMsOf(BarsPeriod bp)
		{
			if (bp == null || bp.Value <= 0) return 0;
			switch (bp.BarsPeriodType)
			{
				case BarsPeriodType.Second: return bp.Value * 1000.0;
				case BarsPeriodType.Minute: return bp.Value * 60000.0;
				// Tick / Volume / Range / Renko carry no intrabar time model at all, and Day+ has
				// nothing to say at 250 ms. Both are refused rather than approximated.
				default: return 0;
			}
		}

		// ---------------------------------------------------------------- device resources

		// Bitmap(RenderTarget, Size2, BitmapProperties), found by signature so no SharpDX.DXGI type
		// is ever named or resolved at compile time. Cached: the lookup runs once per session.
		private static System.Reflection.ConstructorInfo bitmapCtor;
		private static System.Reflection.ConstructorInfo BitmapCtor()
		{
			if (bitmapCtor != null) return bitmapCtor;
			foreach (System.Reflection.ConstructorInfo ci in typeof(SharpDX.Direct2D1.Bitmap).GetConstructors())
			{
				System.Reflection.ParameterInfo[] p = ci.GetParameters();
				if (p.Length == 3
					&& p[0].ParameterType.Name == "RenderTarget"
					&& p[1].ParameterType.Name == "Size2")
				{
					bitmapCtor = ci;
					return bitmapCtor;
				}
			}
			// Loud, not silent: a SharpDX shape change must surface at the first frame, not as a
			// blank chart nobody can explain.
			throw new InvalidOperationException(
				"SizeMap: Bitmap(RenderTarget, Size2, BitmapProperties) not found in SharpDX "
				+ typeof(SharpDX.Direct2D1.Bitmap).Assembly.GetName().Version);
		}

		private bool EnsureBitmap(int w, int h)
		{
			if (_bmp != null && !_bmp.IsDisposed && _bmpW == w && _bmpH == h) return true;
			ReleaseDeviceResources();

			_px = new int[w * h];
			// Taking the format from the target means no SharpDX.DXGI type is NAMED here.
			SharpDX.Direct2D1.BitmapProperties props =
				new SharpDX.Direct2D1.BitmapProperties(RenderTarget.PixelFormat);

			// But not naming one is not enough. `new Bitmap(...)` makes the compiler load every
			// Bitmap constructor for overload resolution, and one of them takes a SharpDX.DXGI.Surface
			// — so the plain `new` is CS0012 even though this file never mentions DXGI. Reflection
			// skips overload resolution entirely and needs no reference.
			//
			// ponytail: binds by signature at runtime. Ceiling — a SharpDX upgrade that changed this
			// constructor would fail at load instead of at compile. Upgrade path: add
			// bin/SharpDX.DXGI.dll in Editor > References and this becomes a plain `new`.
			_bmp = (SharpDX.Direct2D1.Bitmap)BitmapCtor().Invoke(
				new object[] { RenderTarget, new SharpDX.Size2(w, h), props });
			_bmpW = w; _bmpH = h;
			return _bmp != null;
		}

		private void ReleaseDeviceResources()
		{
			if (_bmp != null)
			{
				try { _bmp.Dispose(); } catch (Exception) { }
				_bmp = null;
			}
			_bmpW = _bmpH = 0;
		}

		// ---------------------------------------------------------------- the Phase 1 measurement
		//
		// Phase 1 exists to replace the estimate "~4.4 ms of a ~60 ms budget" with a number measured
		// on Javier's own panel and his own tape. The raster HUD needs a bitmap font that does not
		// exist yet, so it goes to Output — exactly as SizeMapProbe did, so the two are comparable.
		private void Hud(int pw, int ph, RasterView v, double ms)
		{
			_emaMs = _emaMs == 0 ? ms : _emaMs * 0.9 + ms * 0.1;
			if (++_frame % HudEveryFrames != 0) return;

			// The honesty rule made mechanical: at 6 px bars a 250 ms bucket is 0.025 px, so 239 of
			// 240 buckets are invisible and one wins by rounding. Say what a column and a row
			// actually are, every time.
			int columnMs = (int)(ColumnRing.BucketMs * Math.Max(1, Math.Ceiling(1.0 / v.PxPerBucket)));
			int rowTicks = (int)Math.Max(1, Math.Ceiling(1.0 / v.PxPerTick));

			Print(string.Format(
				"SizeMapHeat {0}  {1:0.00}ms (ema {2:0.00})  panel {3}x{4}  col {5}ms  row {6}t  s0 {7} cap {8}  obs {9}L",
				Instrument.MasterInstrument.Name, ms, _emaMs, pw, ph, columnMs, rowTicks,
				Volatile.Read(ref _s0), Volatile.Read(ref _sCap), Volatile.Read(ref _levelsSeen)));

			// PROJ is the row map grading itself against NinjaTrader. err is how far our y for a
			// known price lands from where NT8 puts it, near the top of the visible range: ~0 means
			// the linear map holds, tens of px means every row on screen is in the wrong place.
			Print(string.Format(
				"   PROJ err {0:+0.0;-0.0;0.0}px  pxTick {1:0.000}  span {2:0.00}pts  anchor {3}@{4:0.0}",
				_projErr, _projPxTick, _projSpan, v.AnchorRow, v.AnchorY));

			Print(string.Format(
				"   ring pub {0} ovf {1} late {2}  epochs {3}  rec {4}",
				_ring.PublishedIndex, _ring.Overflows, _ring.LateDrops, Volatile.Read(ref _epochs),
				_rec == null ? "off"
					: string.Format("{0:0.0}MB drop {1} pend {2}{3}{4}",
						_rec.BytesWritten / 1048576.0, _rec.Dropped, _rec.Pending,
						_rec.Capped ? " CAPPED" : "", _rec.Failed ? " FAILED" : "")));
		}

		private static bool IsPlayback()
		{
			try { return NinjaTrader.Cbi.Connection.PlaybackConnection != null; }
			catch (Exception) { return false; }
		}

		#region Properties
		[NinjaScriptProperty]
		[Range(1, 240)]
		[Display(Name = "Minutes of history", Description = "Depth of the column ring. 30 min = 7200 columns ~= 2.9 MB.", Order = 1, GroupName = "SizeMap")]
		public int MinutesOfHistory { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Record raw tape", Description = "Write the depth/tape corpus to Documents/NinjaTrader 8/SizeMap. Depth has no history — a day not recorded is a day that can never be calibrated against.", Order = 2, GroupName = "SizeMap")]
		public bool RecordRaw { get; set; }
		#endregion
	}
}
