#region Using declarations
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SizeMap.Engine;
#endregion

// SizeMapRecorder — the raw depth/tape corpus writer. Format, codec and queue live in
// engine/RawRecord.cs; this file is only the part that cannot be portable: NinjaTrader's
// UserDataDir, a FileStream and a background drain task.
//
// It ships before anything consumes it, on purpose. Depth has no history — OnMarketDepth
// never fires on historical bars — so every threshold in SizeMap can only ever be
// calibrated against tape recorded while it happened. Every day this is not running is a
// day of corpus that will never exist.
//
// THREADING CONTRACT
//   Producer  = NT8's per-instrument depth/data thread (OnMarketDepth / OnMarketData).
//               Does exactly one thing: pack a 16-byte record into a preallocated ring.
//               No allocation, no file handle, no Print, no DateTime.Now — every timestamp
//               is e.Time (market time), which in accelerated Playback is minutes away
//               from wall time.
//   Consumer  = one background Task, 250 ms cadence. Owns every FileStream operation.
//   Handoff   = RawEventQueue's volatile indexes. The producer writes _t0Ticks BEFORE the
//               volatile publish of the first record and only changes it again through an
//               EpochBreak record, so the consumer's read of it is a correct acquire.
//
// A stalled or failed disk must never reach the producer: the queue DROPS with a counter
// rather than blocking (the depth thread is shared with every other chart, DOM and
// strategy on that instrument) or growing without limit (that turns a full disk into an
// OOM). Drops are visible in Dropped and as seq gaps in the file itself.
//
// Log/StateChanged style callbacks are deliberately NOT wired here: unlike BigPrintsRecorder
// this class never invokes a callback while holding anything, but Log is still called from
// the drain thread — the wired handler must be non-blocking (Print, InvokeAsync) and must
// never call back into the recorder.
namespace NinjaTrader.NinjaScript.Indicators
{
	public sealed class SizeMapRecorder
	{
		// Ported from RadarTab.MaybeRunEngine / BigPrintsRecorder.TimeCheckLocked. A Playback
		// slider rewind does NOT re-run DataLoaded, so without this the file splices two tape
		// epochs into one timeline and every calibration built on it is silently wrong.
		private const double BackwardsTolSecs = 2;    // cross-thread timestamp skew, not a rewind
		private const double JumpForwardSecs = 30;    // bigger forward gap than any real quiet tape

		private const int HeartbeatSecs = 60;         // "alive but the tape is quiet" vs "died"
		private const int DrainMs = 250;
		private const int ChunkBytes = 64 * 1024;     // 4096 records
		private const int DefaultSlots = 32768;       // 512 KB ring ~= 80 s of ES depth at 400 ev/s
		private const long DefaultMaxBytesPerDay = 512L * 1024 * 1024;
		private const int MaxWriteRetries = 3;

		public Action<string> Log = delegate { };

		// ---- producer-owned ----
		private readonly RawEventQueue _q;
		private readonly double _tickSize;
		private readonly int _flags;
		private long _t0Ticks;                 // 0 = no epoch yet; see the acquire note above
		private long _firstT0Ticks;            // the FIRST epoch's t0. The drain may not open the
		                                       // file until several epochs later, and the header of
		                                       // part 1 must still describe part 1.
		private DateTime _lastSeen;
		private DateTime _heartbeatAt;
		private bool _breakPending;            // an EpochBreak that did not fit; it is not droppable
		private RawRecord _breakRec;
		private long _skipped;                 // events dropped to keep an unwritten break in order
		private volatile bool _stopping;

		// ---- consumer-owned ----
		private readonly string _dir;
		private readonly string _safeName;
		private readonly byte[] _chunk = new byte[ChunkBytes];
		private readonly byte[] _retry = new byte[ChunkBytes];
		private readonly byte[] _hdr = new byte[RawFile.HeaderBytes];
		private int _retryLen;
		private int _writeFailures;
		private FileStream _fs;
		private string _openPath;
		private long _fileT0Ticks;
		private DateTime _fileDate;
		private int _part = 1;
		private long _bytesToday;
		private Task _drain;

		// ---- HUD ----
		public long Dropped { get { return _q.Dropped + _skipped; } }
		public long BytesWritten { get; private set; }
		public bool Capped { get; private set; }
		public bool Failed { get; private set; }
		public string CurrentPath { get; private set; }
		public int Pending { get { return _q.Count; } }
		public long MaxBytesPerDay = DefaultMaxBytesPerDay;

		public SizeMapRecorder(string instrumentName, double tickSize, bool isReplay)
			: this(instrumentName, tickSize, isReplay,
				   Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "SizeMap"), DefaultSlots)
		{
		}

		// dir/slots are injectable so the harness and the tests can drive the same class.
		public SizeMapRecorder(string instrumentName, double tickSize, bool isReplay, string dir, int slots)
		{
			_tickSize = tickSize > 0 ? tickSize : 0.25;
			_flags = isReplay ? RawFile.FlagReplay : 0;
			_dir = dir;
			_safeName = Sanitize(instrumentName);
			_q = new RawEventQueue(slots);
		}

		public void Start()
		{
			if (_drain != null) return;
			_drain = Task.Run((Action)DrainLoop);
		}

		// Called from State.Terminated (UI thread, never the market thread). Blocks briefly so
		// the tail of the session actually reaches disk instead of dying with the process.
		public void Stop()
		{
			_stopping = true;
			Task d = _drain;
			_drain = null;
			if (d != null) { try { d.Wait(2000); } catch (Exception) { } }
		}

		// ---------------------------------------------------------------- producer side

		public void OnDepth(DepthEvent de)
		{
			if (!Accept(de.Time)) return;
			if (de.IsReset)
			{
				Emit(de.Time, RawKind.FeedReset, 0, 0, RawFile.NA, RawFile.NA);
				return;
			}
			if (de.Price <= 0) return;
			Emit(de.Time, de.Side == Side.Bid ? RawKind.DepthBid : RawKind.DepthAsk,
				 Row(de.Price), de.Volume,
				 de.Position >= 0 && de.Position < RawFile.NA ? (byte)de.Position : RawFile.NA,
				 (byte)de.Op);
		}

		public void OnTrade(TradeEvent te, bool isBuy)
		{
			if (!Accept(te.Time)) return;
			if (te.Price <= 0) return;
			Emit(te.Time, isBuy ? RawKind.TradeBuy : RawKind.TradeSell,
				 Row(te.Price), te.Volume, RawFile.NA, RawFile.NA);
		}

		// SizeMapHeat also detects the discontinuity (it has to rebuild the book), so it can
		// force the break here. Idempotent: Accept has already re-based the epoch by then and
		// the second call is a no-op.
		public void OnEpochBreak(DateTime t)
		{
			if (_stopping || Failed || t == default(DateTime)) return;
			if (_t0Ticks == 0) { Rebase(t); return; }
			if (t.Ticks == _t0Ticks) return;
			Break(t);
		}

		// Returns false when the caller must drop this event. Also owns the epoch clock.
		private bool Accept(DateTime t)
		{
			if (_stopping || Failed || t == default(DateTime)) return false;

			if (_t0Ticks == 0) { Rebase(t); return true; }

			// Note what is NOT tested here: Capped. The cap is per DAY, so the drain has to keep
			// seeing the stream to notice the day boundary that lifts it — in particular the
			// EpochBreak record that carries the new date. While capped the drain discards; the
			// producer pays one 16-byte pack per event for it, which is the cheap side of the trade.

			// The default(DateTime) test above is load-bearing and runs before every other
			// guard: DateTime.MinValue.AddSeconds(-2) throws.
			if (t < _lastSeen.AddSeconds(-BackwardsTolSecs)
				|| (t - _lastSeen).TotalSeconds > JumpForwardSecs
				|| t.Date != _lastSeen.Date)
			{
				// A session/date boundary rolls the file for the same reason a rewind does:
				// one file per instrument per day, and dtMs stays a small int.
				Break(t);
				return true;
			}

			if (t > _lastSeen) _lastSeen = t;
			if (t >= _heartbeatAt)
			{
				_heartbeatAt = t.AddSeconds(HeartbeatSecs);
				Emit(t, RawKind.Heartbeat, 0, 0, RawFile.NA, RawFile.NA);
			}
			return true;
		}

		private void Break(DateTime t)
		{
			// The break is CONTROL data, not a sample: losing it welds two tape epochs into one
			// timeline, which is the one corruption this file exists to prevent. So it is not
			// droppable — if the queue is momentarily full it stays pending and nothing that
			// belongs to the new epoch is queued ahead of it.
			RawRecord brk = RawFile.MakeEpochBreak(RawFile.MsSince(_t0Ticks, _lastSeen), t.Ticks);
			if (!_q.Enqueue(brk)) { _breakPending = true; _breakRec = brk; }
			Rebase(t);
		}

		private void Rebase(DateTime t)
		{
			_t0Ticks = t.Ticks;
			if (_firstT0Ticks == 0) _firstT0Ticks = t.Ticks;   // the first FILE's header t0, fixed forever
			_lastSeen = t;
			_heartbeatAt = t.AddSeconds(HeartbeatSecs);
		}

		private void Emit(DateTime t, RawKind kind, int row, long size, byte pos, byte op)
		{
			if (_breakPending)
			{
				if (!_q.Enqueue(_breakRec)) { _skipped++; return; }   // never reorder around a break
				_breakPending = false;
			}
			RawRecord r = new RawRecord();
			r.DtMs = RawFile.MsSince(_t0Ticks, t);
			r.Row = row;
			r.Size = size > int.MaxValue ? int.MaxValue : (int)size;
			r.Kind = kind;
			r.Pos = pos;
			r.Op = op;
			_q.Enqueue(r);
		}

		private int Row(double price)
		{
			return (int)Math.Round(price / _tickSize, MidpointRounding.AwayFromZero);
		}

		// ---------------------------------------------------------------- consumer side

		private void DrainLoop()
		{
			try
			{
				while (true)
				{
					bool last = _stopping;
					DrainOnce();
					if (last) break;
					Thread.Sleep(DrainMs);
				}
			}
			catch (Exception ex)
			{
				// The recorder is a passenger. It may lose the corpus; it may not take the
				// indicator — or the platform — down with it.
				Failed = true;
				Log("SizeMap recorder: drain thread died — " + ex.Message);
			}
			finally { CloseFile(); }
		}

		private void DrainOnce()
		{
			if (Failed) return;

			if (_retryLen > 0)
			{
				int len = _retryLen;
				_retryLen = 0;
				if (!Flush(_retry, 0, len)) return;   // Flush re-stashes on failure
			}

			int n;
			while ((n = _q.Drain(_chunk, 0, _chunk.Length)) > 0)
			{
				if (!Flush(_chunk, 0, n)) return;
				if (Failed) return;
			}
		}

		// Writes [off,end) splitting the stream at every EpochBreak record, because the record
		// that ends an epoch carries the next epoch's t0 and must be the last one in its file.
		// Returns false when the write failed and the remainder was stashed for the next tick.
		private bool Flush(byte[] buf, int off, int end)
		{
			int i = off;
			while (i < end)
			{
				int brk = RawFile.FindEpochBreak(buf, i, end);
				int stop = brk < 0 ? end : brk + RawFile.RecordBytes;
				// Capped: keep walking the stream, just do not write it. The epoch breaks in it are
				// what tell us the day rolled over and the cap can lift.
				if (!Capped && !WriteBytes(buf, i, stop - i)) { Stash(buf, i, end); return false; }
				i = stop;
				if (brk >= 0)
				{
					long nextT0 = RawFile.EpochT0Of(RawFile.Read(buf, brk));
					CloseFile();
					if (Capped && new DateTime(nextT0).Date != _fileDate) Capped = false;   // per DAY
					_fileT0Ticks = nextT0;
					_part++;
					_reopenSamePath = false;   // a roll opens a NEW part — never append to the closed one
				}
				if (Failed) return true;   // stop hammering a disk that already refused us 3 times
			}
			return true;
		}

		private void Stash(byte[] buf, int off, int end)
		{
			int len = end - off;
			if (len > _retry.Length) len = _retry.Length;
			Buffer.BlockCopy(buf, off, _retry, 0, len);   // handles buf == _retry (memmove)
			_retryLen = len;
		}

		private bool WriteBytes(byte[] buf, int off, int len)
		{
			if (len <= 0) return true;
			try
			{
				if (!EnsureOpen()) return false;
				if (_bytesToday + len > MaxBytesPerDay)
				{
					Capped = true;
					CloseFile();
					Log("SizeMap recorder: daily byte cap reached (" + (MaxBytesPerDay >> 20) + " MB) — recording stopped.");
					return true;
				}
				_fs.Write(buf, off, len);
				_fs.Flush();
				_bytesToday += len;
				BytesWritten += len;
				_writeFailures = 0;
				return true;
			}
			catch (Exception ex)
			{
				CloseFile();
				if (++_writeFailures >= MaxWriteRetries)
				{
					Failed = true;
					Log("SizeMap recorder: write failed " + _writeFailures + "x — recording stopped. " + ex.Message);
					return true;   // stop retrying; the caller must not stash any more
				}
				Log("SizeMap recorder: write failed, retrying next tick — " + ex.Message);
				return false;
			}
		}

		private bool EnsureOpen()
		{
			if (_fs != null) return true;
			if (_fileT0Ticks == 0) _fileT0Ticks = _firstT0Ticks;   // part 1 belongs to the FIRST epoch
			if (_fileT0Ticks == 0) return false;

			DateTime d = new DateTime(_fileT0Ticks);
			if (_fileDate != d.Date) { _fileDate = d.Date; _part = 1; _bytesToday = 0; }

			Directory.CreateDirectory(_dir);

			string path;
			if (_openPath != null && File.Exists(_openPath) && _reopenSamePath)
			{
				path = _openPath;   // retry after a failed write: append, do NOT re-header
				_fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, ChunkBytes, FileOptions.SequentialScan);
				CurrentPath = path;
				return true;
			}

			// A mid-day NT8 restart must not clobber this morning's corpus: walk the part
			// counter up to the first free name instead of truncating.
			while (true)
			{
				path = Path.Combine(_dir, "sm-raw-" + _safeName + "-" + d.ToString("yyyyMMdd")
					+ (_part > 1 ? "-part" + _part : "") + RawFile.Extension);
				if (!File.Exists(path) || new FileInfo(path).Length == 0) break;
				_part++;
			}

			_fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, ChunkBytes, FileOptions.SequentialScan);
			RawFile.WriteHeader(_hdr, 0, _fileT0Ticks, _tickSize, _flags | (_part > 1 ? RawFile.FlagContinuation : 0));
			_fs.Write(_hdr, 0, RawFile.HeaderBytes);
			_bytesToday += RawFile.HeaderBytes;
			BytesWritten += RawFile.HeaderBytes;
			_openPath = path;
			CurrentPath = path;
			_reopenSamePath = true;
			return true;
		}

		private bool _reopenSamePath;

		private void CloseFile()
		{
			if (_fs == null) return;
			try { _fs.Flush(); _fs.Dispose(); } catch (Exception) { }
			_fs = null;
		}

		private static string Sanitize(string s)
		{
			if (string.IsNullOrEmpty(s)) return "UNKNOWN";
			char[] c = s.ToCharArray();
			for (int i = 0; i < c.Length; i++)
				if (!char.IsLetterOrDigit(c[i]) && c[i] != '-' && c[i] != '_') c[i] = '_';
			return new string(c);
		}
	}
}
