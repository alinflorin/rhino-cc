using System;
using System.Diagnostics;

namespace RhinoCC.Core
{
    /// <summary>Debug-build tracing. Calls compile away in Release; the plugin points <see cref="Sink"/> at Rhino's console.</summary>
    public static class Log
    {
        public static Action<string> Sink { get; set; }

        [Conditional("DEBUG")]
        public static void Write(string message)
        {
            try { Sink?.Invoke("[RhinoCC] " + message); }
            catch { /* logging must never break a turn */ }
        }
    }
}
