using System.Collections.Generic;
using System.Windows.Input;

namespace KronosScreenRemote;

static class KeyMap
{
    static readonly Dictionary<Key, int> _map = new()
    {
        { Key.A, 30 }, { Key.B, 48 }, { Key.C, 46 }, { Key.D, 32 }, { Key.E, 18 },
        { Key.F, 33 }, { Key.G, 34 }, { Key.H, 35 }, { Key.I, 23 }, { Key.J, 36 },
        { Key.K, 37 }, { Key.L, 38 }, { Key.M, 50 }, { Key.N, 49 }, { Key.O, 24 },
        { Key.P, 25 }, { Key.Q, 16 }, { Key.R, 19 }, { Key.S, 31 }, { Key.T, 20 },
        { Key.U, 22 }, { Key.V, 47 }, { Key.W, 17 }, { Key.X, 45 }, { Key.Y, 21 },
        { Key.Z, 44 },
        { Key.D1, 2  }, { Key.D2, 3  }, { Key.D3, 4  }, { Key.D4, 5  }, { Key.D5, 6  },
        { Key.D6, 7  }, { Key.D7, 8  }, { Key.D8, 9  }, { Key.D9, 10 }, { Key.D0, 11 },
        // Numpad digits are NOT in the keyboard map — they route to the control surface
        // via BUTTON NUM0..NUM9 (see OnKeyDown capture block in MainWindow.xaml.cs).
        { Key.Escape,   1  },
        { Key.Space,   57  }, { Key.Back,   14  }, { Key.Tab,    15  },
        { Key.Return,  28  }, { Key.Delete, 111 }, { Key.Insert, 110 },
        { Key.Up,    103 }, { Key.Down,  108 }, { Key.Left,  105 }, { Key.Right, 106 },
        { Key.Home,  102 }, { Key.End,   107 }, { Key.Prior, 104 }, { Key.Next,  109 },
        { Key.LeftShift,  42 }, { Key.RightShift,  54 },
        { Key.LeftCtrl,   29 }, { Key.RightCtrl,   97 },
        { Key.LeftAlt,    56 }, { Key.RightAlt,   100 },
        { Key.CapsLock,   58 },
        // Kronos uses a non-US keyboard layout. These remaps send the keycode the Kronos
        // actually maps to the target character rather than the US-layout equivalent.
        // -:/ =:/ ?:| /:\ ;:] ':[
        { Key.OemMinus,        74 },  // - → KEY_KPMINUS (numpad minus; Kronos shows - correctly)
        { Key.OemPlus,         13 },  // = (same situation as -)
        { Key.OemOpenBrackets, 40 },  // [ → send KEY_APOSTROPHE (Kronos maps that to [)
        { Key.Oem6,            39 },  // ] → send KEY_SEMICOLON  (Kronos maps that to ])
        { Key.Oem5,            53 },  // \ → send KEY_SLASH      (Kronos maps that to \)
        { Key.OemSemicolon,    27 },  // ; → send KEY_RIGHTBRACE (best guess for ;)
        { Key.OemQuotes,       26 },  // ' → send KEY_LEFTBRACE  (best guess for ')
        { Key.OemComma,        51 },  // ,
        { Key.OemPeriod,       52 },  // .
        { Key.OemQuestion,     12 },  // / → send KEY_MINUS      (Kronos maps that to /)
        { Key.OemTilde,        41 },  // `
        // Key.Subtract and Key.Decimal omitted — route to control surface in capture block
        { Key.Multiply, 55 }, { Key.Add, 78 }, { Key.Divide, 98 },
        { Key.F1,  59 }, { Key.F2,  60 }, { Key.F3,  61 }, { Key.F4,  62 },
        { Key.F5,  63 }, { Key.F6,  64 }, { Key.F7,  65 }, { Key.F8,  66 },
        { Key.F9,  67 }, { Key.F10, 68 }, { Key.F11, 87 }, { Key.F12, 88 },
    };

    public static int? ToLinux(Key k) =>
        _map.TryGetValue(k, out int code) ? code : null;

    // Shifted overrides: keys where Shift+X on the PC needs special handling because the
    // Kronos layout differs from US.  Code = Linux keycode to send.  KeepShift = whether
    // to leave Shift held when sending Code (true) or bracket it with Shift release/re-press (false).
    //
    // Discovered via inject_test on the Kronos:
    //   No Shift needed (KeepShift=false): * via KEY_KPASTERISK(55), + via KEY_KPPLUS(78)
    //   Shift kept (KeepShift=true):       ? = Shift+KEY_EQUAL(13)
    //
    // Unavailable in Kronos text input: ~  =  _  `
    static readonly Dictionary<Key, (int Code, bool KeepShift)> _shifted = new()
    {
        { Key.D8,          (55, false) },  // Shift+8   → * (KEY_KPASTERISK, no Shift needed)
        { Key.OemPlus,     (78, false) },  // Shift+=   → + (KEY_KPPLUS, no Shift needed)
        { Key.OemQuestion, (13, true)  },  // Shift+/   → ? (Shift+KEY_EQUAL)
    };

    public static (int Code, bool KeepShift)? ToLinuxShifted(Key k) =>
        _shifted.TryGetValue(k, out var v) ? v : null;
}
