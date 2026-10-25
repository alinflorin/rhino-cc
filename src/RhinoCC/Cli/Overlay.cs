using System.Collections.Generic;
using System.Drawing;
using Rhino.Display;
using Rhino.Geometry;

namespace RhinoCC.Cli
{
    /// <summary>Coloured lines drawn over the model (oriented boxes, contact graph). A snapshot: it is not updated when objects move.</summary>
    internal sealed class Overlay : DisplayConduit
    {
        private readonly List<KeyValuePair<Line, Color>> _lines = new List<KeyValuePair<Line, Color>>();

        public static readonly Overlay Boxes = new Overlay();
        public static readonly Overlay Graph = new Overlay();

        public int Count => _lines.Count;

        public void Set(IEnumerable<KeyValuePair<Line, Color>> lines)
        {
            _lines.Clear();
            _lines.AddRange(lines);
            Enabled = _lines.Count > 0;
        }

        public void Clear() => Set(new KeyValuePair<Line, Color>[0]);

        protected override void PostDrawObjects(DrawEventArgs e)
        {
            foreach (var l in _lines) e.Display.DrawLine(l.Key, l.Value, 2);
        }
    }
}
