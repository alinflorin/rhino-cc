using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using RhinoCC.Core.Cli;
using RhinoCC.Core.Geometry;
using RhinoCC.Core.Printing;

namespace RhinoCC.Cli
{
    /// <summary>The piece as the printing logic sees it: all targeted solids as one triangle mesh in millimetres, in the model's own frame.</summary>
    internal sealed class Piece
    {
        public TriMesh Mesh;
        public List<RhinoObject> Objects;
        public double ToMm;
    }

    /// <summary>
    /// Supports and sprue as Rhino objects. The settings live in the document's user strings; the contact points of the supports are
    /// Point objects on the "support-points" layer (the truth: move, delete or add them with Rhino's own tools), and the pillars, the
    /// raft and the sprue are solids that are generated from them and from the settings, on the "supports" and "sprue" layers.
    /// </summary>
    internal static class PrintOps
    {
        public const string SettingsKey = "RhinoCC.print";
        public const string SupportsLayer = "supports", PointsLayer = "support-points", SprueLayer = "sprue";
        public const string SupportsKey = "RhinoCC.supports", SprueKey = "RhinoCC.sprue";
        private const string IdKey = "rhinocc.support", SourceKey = "rhinocc.source", TipKey = "rhinocc.tip", AtKey = "rhinocc.at";

        /// <summary>Raised on the UI thread after the settings or the supports changed.</summary>
        public static event Action Changed;

        public static void RaiseChanged() => Changed?.Invoke();

        // ------------------------------------------------------------ settings

        public static PrintSettings Settings(RhinoDoc doc) => PrintSettings.Parse(doc.Strings.GetValue(SettingsKey));

        public static void SaveSettings(RhinoDoc doc, PrintSettings s)
        {
            doc.Strings.SetString(SettingsKey, s.ToJson());
            RaiseChanged();
        }

        public static double ToMm(RhinoDoc doc) => RhinoMath.UnitScale(doc.ModelUnitSystem, UnitSystem.Millimeters);

        // ------------------------------------------------------------ layers and objects

        private static int Layer(RhinoDoc doc, string name, System.Drawing.Color color)
        {
            var i = doc.Layers.FindByFullPath(name, RhinoMath.UnsetIntIndex);
            if (i == RhinoMath.UnsetIntIndex) i = doc.Layers.Add(name, color);
            return i;
        }

        private static int LayerIndexOrNone(RhinoDoc doc, string name) => doc.Layers.FindByFullPath(name, RhinoMath.UnsetIntIndex);

        private static bool OnLayer(RhinoDoc doc, RhinoObject o, string name)
        {
            var i = LayerIndexOrNone(doc, name);
            return i != RhinoMath.UnsetIntIndex && o.Attributes.LayerIndex == i;
        }

        /// <summary>True for the generated supports, their contact points and the sprue: never part of the piece itself.</summary>
        public static bool IsOurs(RhinoDoc doc, RhinoObject o) =>
            OnLayer(doc, o, SupportsLayer) || OnLayer(doc, o, PointsLayer) || OnLayer(doc, o, SprueLayer);

        private static List<RhinoObject> OnLayerObjects(RhinoDoc doc, string name)
        {
            var i = LayerIndexOrNone(doc, name);
            if (i == RhinoMath.UnsetIntIndex) return new List<RhinoObject>();
            return doc.Objects.FindByLayer(doc.Layers[i])?.ToList() ?? new List<RhinoObject>();
        }

        private static void DeleteAll(RhinoDoc doc, IEnumerable<RhinoObject> objects)
        {
            foreach (var o in objects.ToList()) doc.Objects.Delete(o.Id, true);
        }

        public static List<RhinoObject> DefaultTargets(RhinoDoc doc) =>
            AnalyzeCommand.Select(doc, new CliArgs());

        // ------------------------------------------------------------ the piece

        public static Piece Gather(RhinoDoc doc, IList<RhinoObject> targets)
        {
            targets = targets.Where(o => !IsOurs(doc, o)).ToList();
            if (targets.Count == 0) throw new CliException("nothing to work on: no visible brep, extrusion or mesh object outside the supports and sprue layers");
            var toMm = ToMm(doc);
            var verts = new List<Vec3>();
            var tris = new List<int>();
            foreach (var o in targets)
            {
                var m = MeshOf(o);
                if (m == null) continue;
                m.Faces.ConvertQuadsToTriangles();
                var b = verts.Count;
                for (var i = 0; i < m.Vertices.Count; i++) verts.Add(new Vec3(m.Vertices[i].X * toMm, m.Vertices[i].Y * toMm, m.Vertices[i].Z * toMm));
                for (var i = 0; i < m.Faces.Count; i++)
                {
                    var f = m.Faces[i];
                    tris.Add(b + f.A); tris.Add(b + f.B); tris.Add(b + f.C);
                }
            }
            if (tris.Count == 0) throw new CliException("the objects could not be meshed");
            return new Piece { Mesh = new TriMesh(verts.ToArray(), tris.ToArray()), Objects = targets.ToList(), ToMm = toMm };
        }

        /// <summary>A render mesh with outward normals when the object is a closed solid (the overhang logic reads the normals).</summary>
        private static Mesh MeshOf(RhinoObject o)
        {
            if (o.Geometry is Mesh m)
            {
                var d = m.DuplicateMesh();
                if (m.IsClosed && m.SolidOrientation() < 0) d.Flip(true, true, true);
                return d;
            }
            var b = o.Geometry as Brep ?? (o.Geometry as Extrusion)?.ToBrep(true);
            if (b == null) return null;
            var parts = Mesh.CreateFromBrep(b, MeshingParameters.Default);
            if (parts == null || parts.Length == 0) return null;
            var all = new Mesh();
            all.Append(parts);
            if (b.IsSolid && b.SolidOrientation == BrepSolidOrientation.Inward) all.Flip(true, true, true);
            return all;
        }

        // ------------------------------------------------------------ the contact points

        /// <summary>
        /// The contacts, in millimetres. Points the user drew on the layer get an id and count as manual; the layer is their home,
        /// so everything on it that is a point is a support.
        /// </summary>
        public static List<Support> ReadSupports(RhinoDoc doc)
        {
            var toMm = ToMm(doc);
            if (LayerIndexOrNone(doc, PointsLayer) == RhinoMath.UnsetIntIndex)
            {
                // the points were never drawn, or their layer was removed: the copy in the file brings them back
                var saved = Stored(doc);
                if (saved.Count > 0) WriteSupports(doc, saved);
            }
            var found = new List<KeyValuePair<RhinoObject, Support>>();
            foreach (var o in OnLayerObjects(doc, PointsLayer))
            {
                var pt = o.Geometry as Rhino.Geometry.Point;
                if (pt == null) continue;
                var s = new Support { P = new Vec3(pt.Location.X * toMm, pt.Location.Y * toMm, pt.Location.Z * toMm), Source = "manual" };
                if (int.TryParse(o.Attributes.GetUserString(IdKey), out var id)) s.Id = id;
                var src = o.Attributes.GetUserString(SourceKey);
                if (src == "auto") s.Source = "auto";
                if (double.TryParse(o.Attributes.GetUserString(TipKey), NumberStyles.Float, CultureInfo.InvariantCulture, out var tip)) s.Tip = tip;
                found.Add(new KeyValuePair<RhinoObject, Support>(o, s));
            }
            var next = found.Select(f => f.Value.Id).DefaultIfEmpty(0).Max() + 1;
            var seen = new HashSet<int>();
            foreach (var f in found.OrderBy(f => f.Value.Id == 0 ? int.MaxValue : f.Value.Id))
            {
                if (f.Value.Id == 0 || !seen.Add(f.Value.Id))
                {
                    f.Value.Id = next++; // a point drawn by hand, or a copy of one
                    seen.Add(f.Value.Id);
                    var attrs = f.Key.Attributes.Duplicate();
                    Describe(attrs, f.Value);
                    doc.Objects.ModifyAttributes(f.Key, attrs, true);
                }
            }
            var result = found.Select(f => f.Value).OrderBy(s => s.Id).ToList();
            Store(doc, result);
            return result;
        }

        private sealed class StoredSupport
        {
            public int Id;
            public string Source;
            public double X, Y, Z;
            public double? Tip;
        }

        /// <summary>The supports are also kept in the file's user strings (millimetres), like the conversations; the points are their editable view.</summary>
        private static void Store(RhinoDoc doc, IEnumerable<Support> list)
        {
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(list.Select(x => new StoredSupport { Id = x.Id, Source = x.Source, X = x.P.X, Y = x.P.Y, Z = x.P.Z, Tip = x.Tip }));
            if (doc.Strings.GetValue(SupportsKey) != json) doc.Strings.SetString(SupportsKey, json);
        }

        private static List<Support> Stored(RhinoDoc doc)
        {
            try
            {
                var l = Newtonsoft.Json.JsonConvert.DeserializeObject<List<StoredSupport>>(doc.Strings.GetValue(SupportsKey) ?? "[]");
                return (l ?? new List<StoredSupport>()).Select(x => new Support { Id = x.Id, Source = x.Source ?? "manual", P = new Vec3(x.X, x.Y, x.Z), Tip = x.Tip }).ToList();
            }
            catch (Newtonsoft.Json.JsonException) { return new List<Support>(); }
        }

        public static int NextId(IEnumerable<Support> list) => list.Select(s => s.Id).DefaultIfEmpty(0).Max() + 1;

        private static void Describe(ObjectAttributes attrs, Support s)
        {
            attrs.Name = "support " + s.Id;
            attrs.SetUserString(IdKey, s.Id.ToString(CultureInfo.InvariantCulture));
            attrs.SetUserString(SourceKey, s.Source);
            if (s.Tip.HasValue) attrs.SetUserString(TipKey, s.Tip.Value.ToString(CultureInfo.InvariantCulture));
            else attrs.DeleteUserString(TipKey);
        }

        /// <summary>Makes the point objects match the list: unchanged ones stay, others are replaced, missing ones are added.</summary>
        public static void WriteSupports(RhinoDoc doc, IList<Support> list)
        {
            Store(doc, list);
            var toMm = ToMm(doc);
            var layer = Layer(doc, PointsLayer, System.Drawing.Color.FromArgb(220, 40, 40));
            var existing = new Dictionary<int, RhinoObject>();
            var strays = new List<RhinoObject>();
            foreach (var o in OnLayerObjects(doc, PointsLayer))
            {
                if (o.Geometry is Rhino.Geometry.Point && int.TryParse(o.Attributes.GetUserString(IdKey), out var id) && !existing.ContainsKey(id)) existing[id] = o;
                else strays.Add(o);
            }
            DeleteAll(doc, strays);
            var wanted = new HashSet<int>(list.Select(s => s.Id));
            DeleteAll(doc, existing.Where(e => !wanted.Contains(e.Key)).Select(e => e.Value));
            foreach (var s in list)
            {
                var at = new Point3d(s.P.X / toMm, s.P.Y / toMm, s.P.Z / toMm);
                if (existing.TryGetValue(s.Id, out var o))
                {
                    var same = ((Rhino.Geometry.Point)o.Geometry).Location.DistanceTo(at) < 1e-9
                        && o.Attributes.GetUserString(SourceKey) == s.Source
                        && o.Attributes.GetUserString(TipKey) == (s.Tip.HasValue ? s.Tip.Value.ToString(CultureInfo.InvariantCulture) : null);
                    if (same) continue;
                    doc.Objects.Delete(o.Id, true);
                }
                var attrs = new ObjectAttributes { LayerIndex = layer };
                Describe(attrs, s);
                doc.Objects.AddPoint(at, attrs);
            }
        }

        // ------------------------------------------------------------ pillars, raft, sprue

        public static int ClearSupportSolids(RhinoDoc doc)
        {
            var old = OnLayerObjects(doc, SupportsLayer);
            DeleteAll(doc, old);
            return old.Count;
        }

        private static void ShowPoints(RhinoDoc doc, bool on)
        {
            var i = LayerIndexOrNone(doc, PointsLayer);
            if (i == RhinoMath.UnsetIntIndex || doc.Layers[i].IsVisible == on) return;
            var layer = doc.Layers[i];
            layer.IsVisible = on;
            doc.Layers.Modify(layer, i, true);
        }

        /// <summary>Regenerates the pillars and the raft from the contact points. Returns (pillars, raft pieces).</summary>
        public static KeyValuePair<int, int> BuildSupports(RhinoDoc doc, Piece piece, PrintSettings s)
        {
            ClearSupportSolids(doc);
            var list = ReadSupports(doc);
            if (!s.Supports.Enabled) { ShowPoints(doc, false); return new KeyValuePair<int, int>(0, 0); }
            ShowPoints(doc, true);
            if (list.Count == 0) return new KeyValuePair<int, int>(0, 0);
            var plan = Core.Printing.Supports.Plan(piece.Mesh, list, PrintParams.From(s));
            var layer = Layer(doc, SupportsLayer, System.Drawing.Color.FromArgb(255, 150, 20));
            var tol = doc.ModelAbsoluteTolerance;
            var k = 1 / piece.ToMm;
            Func<Vec3, Point3d> pt = v => new Point3d(v.X * k, v.Y * k, v.Z * k);
            int pillars = 0, raft = 0;
            foreach (var p in plan.Pillars)
            {
                var bottom = pt(p.Bottom); var top = pt(p.Top);
                var len = bottom.DistanceTo(top);
                var b = Revolved(bottom, top - bottom, new[] { Tuple.Create(0.0, p.BottomRadius * k), Tuple.Create(len, p.TopRadius * k) });
                if (b == null) continue;
                var attrs = new ObjectAttributes { LayerIndex = layer, Name = "support pillar " + p.Support.Id };
                attrs.SetUserString(IdKey, p.Support.Id.ToString(CultureInfo.InvariantCulture));
                doc.Objects.AddBrep(b, attrs);
                pillars++;
            }
            var up = new Vector3d(plan.Up.X, plan.Up.Y, plan.Up.Z);
            foreach (var r in plan.Raft)
            {
                var pts = r.Outline.Select(pt).ToList();
                pts.Add(pts[0]);
                var srf = Surface.CreateExtrusion(new PolylineCurve(pts), up * (r.Thickness * k));
                var b = srf?.ToBrep();
                if (b == null) continue;
                b = b.CapPlanarHoles(tol) ?? b;
                if (b.IsSolid && b.SolidOrientation == BrepSolidOrientation.Inward) b.Flip();
                doc.Objects.AddBrep(b, new ObjectAttributes { LayerIndex = layer, Name = "support raft" });
                raft++;
            }
            return new KeyValuePair<int, int>(pillars, raft);
        }

        /// <summary>A solid of revolution about the axis from <paramref name="origin"/> along <paramref name="axis"/>; rings are (distance along the axis, radius).</summary>
        private static Brep Revolved(Point3d origin, Vector3d axis, IList<Tuple<double, double>> rings)
        {
            axis.Unitize();
            var helper = Math.Abs(axis.Z) < 0.9 ? Vector3d.ZAxis : Vector3d.XAxis;
            var perp = Vector3d.CrossProduct(axis, helper);
            perp.Unitize();
            var profile = new PolylineCurve(rings.Select(r => origin + axis * r.Item1 + perp * r.Item2).ToList());
            var rev = RevSurface.Create(profile, new Line(origin, origin + axis), 0, 2 * Math.PI);
            var b = rev == null ? null : Brep.CreateFromRevSurface(rev, true, true);
            if (b != null && b.IsSolid && b.SolidOrientation == BrepSolidOrientation.Inward) b.Flip();
            return b;
        }

        /// <summary>
        /// Puts the sprue stub on the piece, or removes it when the sprue is off. Returns where it went (null when off, or when no
        /// spot was free).
        /// </summary>
        public static SprueSite BuildSprue(RhinoDoc doc, Piece piece, PrintSettings s)
        {
            DeleteAll(doc, OnLayerObjects(doc, SprueLayer));
            if (!s.Sprue.Enabled) { doc.Strings.Delete(SprueKey); return null; }
            var toMm = piece.ToMm;
            Vec3? at = s.Sprue.At == null ? (Vec3?)null : new Vec3(s.Sprue.At[0] * toMm, s.Sprue.At[1] * toMm, s.Sprue.At[2] * toMm);
            var site = Sprue.Plan(piece.Mesh, s.Sprue.Diameter, s.Sprue.Length, at);
            if (site == null) return null;
            var k = 1 / toMm;
            // where it went, kept in the file: "auto" is a result worth remembering, not something to guess again at every opening
            doc.Strings.SetString(SprueKey, Newtonsoft.Json.JsonConvert.SerializeObject(new { mode = site.Manual ? "manual" : "auto", at = new[] { site.At.X * k, site.At.Y * k, site.At.Z * k }, normal = new[] { site.Normal.X, site.Normal.Y, site.Normal.Z } }));
            var origin = new Point3d(site.At.X * k, site.At.Y * k, site.At.Z * k);
            var rings = Sprue.Rings(s.Sprue.Diameter, s.Sprue.Length).Select(r => Tuple.Create(r.h * k, r.r * k)).ToList();
            var b = Revolved(origin, new Vector3d(site.Normal.X, site.Normal.Y, site.Normal.Z), rings);
            if (b == null) return site;
            var attrs = new ObjectAttributes { LayerIndex = Layer(doc, SprueLayer, System.Drawing.Color.FromArgb(40, 120, 220)), Name = "sprue" };
            attrs.SetUserString(AtKey, Fmt.P(origin));
            doc.Objects.AddBrep(b, attrs);
            return site;
        }

        /// <summary>Where the sprue stub is now (model units), or null when there is none.</summary>
        public static Point3d? CurrentSprue(RhinoDoc doc)
        {
            try
            {
                var j = Newtonsoft.Json.Linq.JObject.Parse(doc.Strings.GetValue(SprueKey) ?? "");
                var a = j["at"].ToObject<double[]>();
                return new Point3d(a[0], a[1], a[2]);
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// Makes the geometry follow a change of settings: pillars when anything they depend on changed, the sprue when it did,
        /// and nothing when neither is involved. Quiet about problems (no piece yet): the settings themselves are still kept.
        /// </summary>
        public static string Follow(RhinoDoc doc, PrintSettings before, PrintSettings after)
        {
            var notes = new List<string>();
            var sup = JsonSig(before.Supports) != JsonSig(after.Supports) || before.Tilt != after.Tilt;
            var spr = JsonSig(before.Sprue) != JsonSig(after.Sprue);
            if (!sup && !spr) return "";
            var rec = doc.BeginUndoRecord("RhinoCC print settings");
            try
            {
                Piece piece = null;
                try { piece = Gather(doc, DefaultTargets(doc)); }
                catch (CliException) { /* nothing to build on */ }
                if (sup)
                {
                    if (!after.Supports.Enabled) { var n = ClearSupportSolids(doc); ShowPoints(doc, false); if (n > 0) notes.Add("supports hidden"); }
                    else if (piece != null) { var r = BuildSupports(doc, piece, after); notes.Add($"supports rebuilt ({r.Key} pillars)"); }
                }
                if (spr)
                {
                    if (!after.Sprue.Enabled) DeleteAll(doc, OnLayerObjects(doc, SprueLayer));
                    else if (piece != null)
                    {
                        var site = BuildSprue(doc, piece, after);
                        notes.Add(site == null ? "no free spot for a sprue" : "sprue placed");
                    }
                }
            }
            finally { doc.EndUndoRecord(rec); }
            doc.Views.Redraw();
            return string.Join(", ", notes);
        }

        private static string JsonSig(object o) => Newtonsoft.Json.JsonConvert.SerializeObject(o);

        /// <summary>The point of the piece's surface closest to <paramref name="p"/> (millimetres).</summary>
        public static Vec3 Snap(TriMesh m, Vec3 p)
        {
            var best = p; var bd = double.MaxValue;
            for (var t = 0; t < m.TriCount; t++)
            {
                m.Tri(t, out var a, out var b, out var c);
                var q = TriMesh.ClosestOnTri(p, a, b, c);
                var d = q.DistanceTo(p);
                if (d < bd) { bd = d; best = q; }
            }
            return best;
        }
    }
}
