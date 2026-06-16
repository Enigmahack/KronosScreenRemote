using System.Windows;

namespace KronosScreenRemote;

public partial class MainWindow
{
    // ── Palette helpers ───────────────────────────────────────────────────────

    PaletteEntry EffRgb(int i) => OverlayRenderer.EffRgb(_basePal, _overrides, i);

    int? RawIdxAt(Point screen)
    {
        if (_rawFrame == null) return null;
        double fx = _frameRect.X, fy = _frameRect.Y,
               fw = _frameRect.Width, fh = _frameRect.Height;
        if (fw <= 0 || fh <= 0 ||
            screen.X < fx || screen.X >= fx + fw ||
            screen.Y < fy || screen.Y >= fy + fh) return null;
        int px = Math.Clamp((int)((screen.X - fx) / fw * _frameW), 0, _frameW - 1);
        int py = Math.Clamp((int)((screen.Y - fy) / fh * _frameH), 0, _frameH - 1);
        return _rawFrame[py * _frameW + px];
    }

    // ── History helpers ───────────────────────────────────────────────────────

    void HistTruncateFuture()
    {
        if (_histPos < _history.Count - 1)
            _history.RemoveRange(_histPos + 1, _history.Count - _histPos - 1);
    }

    void HistPush(int idx, PaletteEntry? oldVal, PaletteEntry? newVal, bool merge = false)
    {
        HistTruncateFuture();
        if (merge && _history.Count > 0 && _history[^1].Idx == idx)
            _history[^1] = _history[^1] with { NewVal = newVal };
        else
            _history.Add(new HistEntry(idx, oldVal, newVal));
        _histPos = _history.Count - 1;
    }

    void HistUndo()
    {
        if (_histPos < 0) return;
        var e = _history[_histPos--];
        if (e.OldLocked.HasValue)
        {
            if (e.OldLocked.Value) _locked.Add(e.Idx);
            else _locked.Remove(e.Idx);
            Storage.SaveLocks(_locked);
        }
        else
        {
            if (e.OldVal == null) _overrides.Remove(e.Idx);
            else _overrides[e.Idx] = e.OldVal.Value;
            RebuildLut(); ApplyLut();
        }
    }

    void HistRedo()
    {
        if (_histPos >= _history.Count - 1) return;
        var e = _history[++_histPos];
        if (e.NewLocked.HasValue)
        {
            if (e.NewLocked.Value) _locked.Add(e.Idx);
            else _locked.Remove(e.Idx);
            Storage.SaveLocks(_locked);
        }
        else
        {
            if (e.NewVal == null) _overrides.Remove(e.Idx);
            else _overrides[e.Idx] = e.NewVal.Value;
            RebuildLut(); ApplyLut();
        }
    }

    void HistPushLock(int idx, bool oldLocked, bool newLocked)
    {
        HistTruncateFuture();
        _history.Add(new HistEntry(idx, null, null, oldLocked, newLocked));
        _histPos = _history.Count - 1;
    }

    void SetChannel(int ch, int val)
    {
        if (_locked.Contains(_edSel)) return;
        var old = _overrides.TryGetValue(_edSel, out var ov) ? ov : (PaletteEntry?)null;
        var rgb = EffRgb(_edSel);
        var arr = new[] { (int)rgb.R, (int)rgb.G, (int)rgb.B };
        arr[ch] = Math.Clamp(val, 0, 255);
        var nv = new PaletteEntry((byte)arr[0], (byte)arr[1], (byte)arr[2]);
        _overrides[_edSel] = nv;
        HistPush(_edSel, old, nv, merge: true);
        RebuildLut(); ApplyLut();
    }

    void DeltaChannel(int delta)
    {
        var rgb = EffRgb(_edSel);
        var arr = new[] { (int)rgb.R, (int)rgb.G, (int)rgb.B };
        SetChannel(_edCh, arr[_edCh] + delta);
    }
}
