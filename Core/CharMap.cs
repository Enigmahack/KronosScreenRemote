using System.Collections.Generic;

namespace KronosScreenRemote;

// Maps printable ASCII (+ Tab/Enter/Backspace) to Kronos KEY command sequences.
// Derived from KeyMap.cs remaps — captures which linux keycode + shift state produces each char.
//
// Eva (Kronos UI) text-field case behavior (confirmed via inject_test 2026-06-09):
//   unshifted letter key → UPPERCASE in Eva text field
//   Shift + letter key   → lowercase in Eva text field
// This is the OPPOSITE of standard keyboard convention. All letter entries reflect this.
static class CharMap
{
    public static string[]? GetCommands(char c)
    {
        if (!_map.TryGetValue(c, out var e)) return null;
        return e.Shift
            ? ["KEY 42 1", $"KEY {e.Code} 1", $"KEY {e.Code} 0", "KEY 42 0"]
            : [$"KEY {e.Code} 1", $"KEY {e.Code} 0"];
    }

    public static string GetDescription(char c) =>
        _map.TryGetValue(c, out var e)
            ? (e.Shift ? $"KEY {e.Code} + Shift" : $"KEY {e.Code}")
            : "(no mapping)";

    record Entry(int Code, bool Shift);

    static readonly Dictionary<char, Entry> _map = new()
    {
        { '\b', new(14,  false) },  // Backspace  KEY_BACKSPACE
        { '\t', new(15,  false) },  // Tab        KEY_TAB
        { '\n', new(28,  false) },  // Enter      KEY_ENTER
        { ' ',  new(57,  false) },  // Space      KEY_SPACE
        { '!',  new(2,   true)  },  // Shift+1
        // '"' — Shift+26 triggers Kronos character picker; no direct Eva key confirmed
        { '#',  new(43,  false) },  // KEY_BACKSLASH — confirmed via inject_test
        { '$',  new(5,   true)  },  // Shift+4
        { '%',  new(6,   true)  },  // Shift+5
        { '&',  new(8,   true)  },  // Shift+7 — confirmed Eva UI
        // '\'' — Shift+43 gives ' on console but NOT in Eva; no direct Eva key confirmed
        { '(',  new(10,  true)  },  // Shift+9
        { ')',  new(11,  true)  },  // Shift+0
        { '*',  new(55,  false) },  // shifted override: KEY_KPASTERISK, no shift
        { '+',  new(78,  false) },  // shifted override: KEY_KPPLUS, no shift
        { ',',  new(51,  false) },  // OemComma
        { '-',  new(74,  false) },  // OemMinus → KEY_KPMINUS
        { '.',  new(52,  false) },  // OemPeriod
        { '/',  new(12,  false) },  // OemQuestion → KEY_MINUS
        { '0',  new(11,  false) }, { '1',  new(2,  false) }, { '2',  new(3,  false) },
        { '3',  new(4,   false) }, { '4',  new(5,  false) }, { '5',  new(6,  false) },
        { '6',  new(7,   false) }, { '7',  new(8,  false) }, { '8',  new(9,  false) },
        { '9',  new(10,  false) },
        { ':',  new(27,  true)  },  // Shift+OemSemicolon → KEY_RIGHTBRACE + Shift
        { ';',  new(27,  false) },  // OemSemicolon → KEY_RIGHTBRACE
        { '<',  new(51,  true)  },  // Shift+OemComma
        { '=',  new(13,  false) },  // OemPlus → KEY_EQUAL
        { '>',  new(52,  true)  },  // Shift+OemPeriod
        { '?',  new(13,  true)  },  // shifted override: KEY_EQUAL + Shift
        { '@',  new(3,   true)  },  // Shift+2 — confirmed Eva UI
        // Uppercase letters: unshifted key → uppercase in Eva (inverted case convention)
        { 'A',  new(30,  false) }, { 'B',  new(48, false) }, { 'C',  new(46, false) },
        { 'D',  new(32,  false) }, { 'E',  new(18, false) }, { 'F',  new(33, false) },
        { 'G',  new(34,  false) }, { 'H',  new(35, false) }, { 'I',  new(23, false) },
        { 'J',  new(36,  false) }, { 'K',  new(37, false) }, { 'L',  new(38, false) },
        { 'M',  new(50,  false) }, { 'N',  new(49, false) }, { 'O',  new(24, false) },
        { 'P',  new(25,  false) }, { 'Q',  new(16, false) }, { 'R',  new(19, false) },
        { 'S',  new(31,  false) }, { 'T',  new(20, false) }, { 'U',  new(22, false) },
        { 'V',  new(47,  false) }, { 'W',  new(17, false) }, { 'X',  new(45, false) },
        { 'Y',  new(21,  false) }, { 'Z',  new(44, false) },
        { '[',  new(40,  false) },  // OemOpenBrackets → KEY_APOSTROPHE
        { '\\', new(53,  false) },  // Oem5 → KEY_SLASH
        { ']',  new(39,  false) },  // Oem6 → KEY_SEMICOLON
        { '^',  new(7,   true)  },  // Shift+6
        { '_',  new(74,  true)  },  // Shift+OemMinus → KEY_KPMINUS + Shift
        // '`' (41 unshifted) not accepted by Kronos text fields — omitted
        // Lowercase letters: Shift + key → lowercase in Eva (inverted case convention)
        { 'a',  new(30,  true)  }, { 'b',  new(48, true)  }, { 'c',  new(46, true)  },
        { 'd',  new(32,  true)  }, { 'e',  new(18, true)  }, { 'f',  new(33, true)  },
        { 'g',  new(34,  true)  }, { 'h',  new(35, true)  }, { 'i',  new(23, true)  },
        { 'j',  new(36,  true)  }, { 'k',  new(37, true)  }, { 'l',  new(38, true)  },
        { 'm',  new(50,  true)  }, { 'n',  new(49, true)  }, { 'o',  new(24, true)  },
        { 'p',  new(25,  true)  }, { 'q',  new(16, true)  }, { 'r',  new(19, true)  },
        { 's',  new(31,  true)  }, { 't',  new(20, true)  }, { 'u',  new(22, true)  },
        { 'v',  new(47,  true)  }, { 'w',  new(17, true)  }, { 'x',  new(45, true)  },
        { 'y',  new(21,  true)  }, { 'z',  new(44, true)  },
        { '{',  new(40,  true)  },  // Shift+OemOpenBrackets → KEY_APOSTROPHE + Shift
        { '|',  new(53,  true)  },  // Shift+Oem5 → KEY_SLASH + Shift
        { '}',  new(39,  true)  },  // Shift+Oem6 → KEY_SEMICOLON + Shift
        // '~' — Shift+41 opens Eva's argument editor; omitted to prevent UI hijack
    };
}
